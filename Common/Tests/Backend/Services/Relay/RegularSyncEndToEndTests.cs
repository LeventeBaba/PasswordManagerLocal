using Microsoft.Extensions.DependencyInjection;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using PasswordManagerLocal.Common.Backend.Abstractions.Repositories;
using PasswordManagerLocal.Common.Backend.Abstractions.Services;
using PasswordManagerLocal.Common.Backend.Models;
using PasswordManagerLocal.Common.Backend.Sync;
using PasswordManagerLocal.Common.Tests.TestInfrastructure;
using System.Text;
using System.Text.Json;
using PasswordManagerLocal.Common.Backend.Utils;

namespace PasswordManagerLocal.Common.Tests.Backend.Services;

public sealed partial class RegularSyncEndToEndTests
{
    [TestMethod]
    [TestCategory("Backend")]
    [TestCategory("Integration")]
    [Timeout(ThreeDeviceTestTimeoutMilliseconds)]
    [TestCategory("SyncStressExtended")]
    [TestCategory("Security")]
    public async Task LockedRelayRestart_PreservesOriginEnvelopeAndStoredKnowledge_ThenAllDevicesMerge()
    {
        if (Environment.GetEnvironmentVariable("PASSWORDMANAGERLOCAL_SYNC_STRESS") != "1")
            Assert.Inconclusive("Set PASSWORDMANAGERLOCAL_SYNC_STRESS=1 to run locked-relay restart validation.");
        await using var setup = await CreateThreeDeviceSetupAsync();
        await SynchronizeThirdMembershipAsync(setup);
        await setup.Middle.Endpoints.GetSavedPasswordsAsync(setup.MiddleToken);
        await setup.NewDevice.Endpoints.GetSavedPasswordsAsync(setup.NewDeviceToken);
        foreach (var host in setup.Hosts) host.Transport.Disconnect();
        await setup.Middle.Endpoints.LogoutAsync(setup.MiddleToken);
        var userId = GetLifecycleUserId(setup);
        const string name = "Locked relay restart credential";
        var secret = Encoding.UTF8.GetBytes("locked-relay-test-secret");
        await AddPasswordAsync(setup.Original, setup.OriginalToken, name, secret);
        UserSnapshotEnvelope original;
        using (var scope = setup.Original.Services.CreateScope())
        {
            var user = (await scope.ServiceProvider.GetRequiredService<IUserRepository>().GetByIdWithRelationsAsync(userId))!;
            var row = await scope.ServiceProvider.GetRequiredService<IUserSnapshotPublisherService>().GetOrCreateAsync(user);
            original = JsonSerializer.Deserialize(row.EnvelopePayload, BackendJsonSerializerContext.Default.UserSnapshotEnvelope)!;
        }
        setup.Original.ConnectTo(setup.Middle);
        setup.Middle.ConnectTo(setup.Original);
        await SynchronizeOneWayUntilAsync(setup.Original, setup.Middle, "locked relay durably stores the origin snapshot", async () =>
        {
            using var scope = setup.Middle.Services.CreateScope();
            var row = await scope.ServiceProvider.GetRequiredService<IUserSyncSnapshotRepository>().GetAsync(userId,
                original.OriginDeviceId, original.OriginInstanceId, original.UserKeyEpoch);
            return row is not null && row.OriginRevision >= original.OriginRevision;
        });
        // A bidirectional anti-entropy session can incorporate pre-existing B evidence and
        // publish a newer A revision. Freeze the actual delivered A envelope after the worker
        // drains rather than assuming the pre-session revision remains the latest publication.
        using (var scope = setup.Middle.Services.CreateScope())
        {
            var delivered = (await scope.ServiceProvider.GetRequiredService<IUserSyncSnapshotRepository>().GetAsync(userId,
                original.OriginDeviceId, original.OriginInstanceId, original.UserKeyEpoch))!;
            var received = JsonSerializer.Deserialize(delivered.EnvelopePayload, BackendJsonSerializerContext.Default.UserSnapshotEnvelope)!;
            Assert.AreEqual(setup.Original.Identity.LocalDeviceId, received.OriginDeviceId);
            Assert.AreEqual(setup.Original.Identity.OriginInstanceId, received.OriginInstanceId);
            Assert.IsTrue(received.OriginRevision >= original.OriginRevision);
            UserSnapshotEnvelopeUtil.VerifyWithSigningKey(received, setup.Original.Identity.SignPublicKey);
            original = received;
        }
        await AssertLockedKnowledgeAsync(setup.Middle, original);
        setup.Original.Transport.Disconnect();
        setup.Middle.Transport.Disconnect();
        await using var restartedRelay = await setup.Middle.RestartAsync();
        await AssertLockedKnowledgeAsync(restartedRelay, original);
        restartedRelay.ConnectTo(setup.NewDevice);
        setup.NewDevice.ConnectTo(restartedRelay);
        await SynchronizeOneWayUntilAsync(restartedRelay, setup.NewDevice, "locked restarted relay forwards signed evidence to C", async () =>
            (await setup.NewDevice.Endpoints.GetSavedPasswordsAsync(setup.NewDeviceToken)).Passwords.Any(item => item.Name == name));
        restartedRelay.Transport.Disconnect();
        setup.NewDevice.Transport.Disconnect();
        await RunStepAsync("drain relay sessions before unlocking the persisted snapshot", async ct =>
        {
            await restartedRelay.SyncTasks.StopAllAsync(ct);
            await setup.NewDevice.SyncTasks.StopAllAsync(ct);
        });
        await AssertLockedKnowledgeAsync(restartedRelay, original);
        Assert.IsTrue(restartedRelay.OwnsPostLoginConsumer,
            "The restarted relay must own the consumer that merges its persisted snapshots after login.");
        var relayToken = await restartedRelay.Endpoints.LoginAsync(restartedRelay.CreateLoginRequest(setup.Username));
        // Login exposes the verified local bundle first. Its initial list read releases
        // deferred reconciliation; that read does not wait for the stored snapshot merge.
        await restartedRelay.Endpoints.GetSavedPasswordsAsync(relayToken);
        await RunStepAsync("unlocked relay merges persisted evidence without another delivery", async ct =>
        {
            while (!(await restartedRelay.Endpoints.GetSavedPasswordsAsync(relayToken, ct)).Passwords
                       .Any(item => item.Name == name))
                await Task.Delay(10, ct);
        });
        await AssertPasswordSecretAsync(restartedRelay, relayToken, name, secret);
        await AssertPasswordSecretAsync(setup.NewDevice, setup.NewDeviceToken, name, secret);
        foreach (var host in new[] { restartedRelay, setup.NewDevice })
        {
            using var scope = host.Services.CreateScope();
            var knowledge = await scope.ServiceProvider.GetRequiredService<IUserRevisionKnowledgeRepository>().GetAsync(userId,
                original.OriginDeviceId, original.OriginInstanceId, original.UserKeyEpoch);
            Assert.IsNotNull(knowledge);
            Assert.IsTrue(knowledge.HighestMergedRevision >= original.OriginRevision);
        }
    }

    private static async Task AssertLockedKnowledgeAsync(ProductionSyncTestHost host, UserSnapshotEnvelope original)
    {
        using var scope = host.Services.CreateScope();
        var row = await scope.ServiceProvider.GetRequiredService<IUserSyncSnapshotRepository>().GetAsync(original.UserId,
            original.OriginDeviceId, original.OriginInstanceId, original.UserKeyEpoch);
        Assert.IsNotNull(row);
        Assert.AreEqual(UserSyncSnapshotStatus.Pending, row.Status);
        Assert.AreEqual(original.OriginRevision, row.OriginRevision);
        CollectionAssert.AreEqual(original.SnapshotHash, row.SnapshotHash);
        var relayed = JsonSerializer.Deserialize(row.EnvelopePayload, BackendJsonSerializerContext.Default.UserSnapshotEnvelope)!;
        CollectionAssert.AreEqual(original.OriginSignature, relayed.OriginSignature);
        UserSnapshotEnvelopeUtil.VerifyWithSigningKey(relayed, original.OriginSignPublicKey);
        var knowledge = await scope.ServiceProvider.GetRequiredService<IUserRevisionKnowledgeRepository>().GetAsync(original.UserId,
            original.OriginDeviceId, original.OriginInstanceId, original.UserKeyEpoch);
        Assert.IsNotNull(knowledge);
        Assert.IsTrue(knowledge.HighestStoredRevision >= original.OriginRevision);
        Assert.IsTrue(knowledge.HighestMergedRevision < original.OriginRevision,
            "Custody of ciphertext cannot acknowledge a locked relay's canonical merge.");
    }

}
