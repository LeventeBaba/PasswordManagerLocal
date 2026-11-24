using Microsoft.Extensions.DependencyInjection;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using PasswordManagerLocal.Common.Backend.Abstractions.Repositories;
using PasswordManagerLocal.Common.Backend.Abstractions.Services;
using PasswordManagerLocal.Common.Backend.Models;
using PasswordManagerLocal.Common.Backend.Sync;
using PasswordManagerLocal.Common.Contracts.Errors;
using PasswordManagerLocal.Common.Tests.TestInfrastructure;
using System.Text;

namespace PasswordManagerLocal.Common.Tests.Backend.Services;

public sealed partial class RegularSyncEndToEndTests
{
    [TestMethod]
    [Timeout(ThreeDeviceTestTimeoutMilliseconds)]
    [TestCategory("Recovery")]
    [TestCategory("TransactionHardening")]
    [TestCategory("SyncStressExtended")]
    public async Task RecoveredCanonicalPublication_LockedPeerRestart_PreservesCustodyAndEventuallyMerges()
    {
        if (Environment.GetEnvironmentVariable("PASSWORDMANAGERLOCAL_SYNC_STRESS") != "1")
            Assert.Inconclusive("Set PASSWORDMANAGERLOCAL_SYNC_STRESS=1 for recovery and locked-peer restart validation.");
        await using var pair = await CreateEnrolledPairAsync();
        await pair.Source.Endpoints.GetSavedPasswordsAsync(pair.SourceToken);
        await pair.Target.Endpoints.GetSavedPasswordsAsync(pair.TargetToken);
        pair.Source.Transport.Disconnect();
        pair.Target.Transport.Disconnect();
        await RunStepAsync("drain peers before canonical damage", async ct =>
        {
            await pair.Source.SyncTasks.StopAllAsync(ct);
            await pair.Target.SyncTasks.StopAllAsync(ct);
        });
        const string name = "Recovered private target credential";
        var secret = Encoding.UTF8.GetBytes("recovery-convergence-secret");
        await AddPasswordAsync(pair.Target, pair.TargetToken, name, secret);
        // The root will be deliberately corrupted. Recovery may preserve this edit
        // only from an authenticated retained snapshot, not an unverified child blob.
        await pair.Target.PublishCurrentUserSnapshotAsync();
        Guid userId;
        string username;
        UserDataRecoveryResult recovered;
        using (var scope = pair.Target.Services.CreateScope())
        {
            var users = scope.ServiceProvider.GetRequiredService<IUserRepository>();
            userId = (await users.ListUserIdsAsync()).Single();
            var user = (await users.GetByIdAsync(userId))!;
            var evidence = await scope.ServiceProvider.GetRequiredService<IUserSnapshotPublisherService>()
                .GetLatestAsync(userId, user.KeyEpoch)
                ?? throw new AssertFailedException("The latest local edit has no retained recovery snapshot.");
            var evidenceRevision = evidence.OriginRevision;
            Assert.AreEqual(UserSyncSnapshotStatus.LocalPublished, evidence.Status);
            Assert.IsTrue(scope.ServiceProvider.GetRequiredService<IUserSyncKeyResolverService>()
                .TryResolve(user, out var key, out var confidence));
            Assert.IsNotNull(key);
            using (key)
            {
                using (var verification = await scope.ServiceProvider.GetRequiredService<IUserDataBundleVerificationService>()
                           .VerifyCanonicalAsync(user, key, confidence))
                {
                    Assert.IsTrue(verification.IsHealthy);
                    username = verification.VerifiedBundle!.GeneralUserData.Username;
                }
                var damaged = user.EncryptedPayload.ToArray();
                damaged[0] ^= 0x55;
                await scope.ServiceProvider.ExecuteFixtureSqlAsync(
                    "UPDATE Users SET EncryptedPayload = $payload WHERE UId = $user;", ("$payload", damaged), ("$user", userId));
                recovered = await scope.ServiceProvider.GetRequiredService<IUserDataRecoveryCoordinator>()
                    .TryRecoverAsync(userId, key, confidence, UserDataRecoveryTrigger.ManualRetry);
                Assert.AreEqual(UserDataRecoveryState.Recovered, recovered.State);
                Assert.IsTrue(recovered.RecoveryRevision > evidenceRevision,
                    "Recovery must publish a newer revision of the retained verified data.");
                var durable = (await users.GetByIdAsNoTrackingAsync(userId))!;
                var checkpoint = (await scope.ServiceProvider.GetRequiredService<IUserCanonicalCheckpointRepository>().GetAsync(userId))!;
                UserCanonicalCheckpointUtil.Verify(durable, checkpoint, pair.Target.Identity);
                Assert.AreEqual(UserLoginIdentityStatus.Active,
                    (await users.GetLoginIdentityStateAsync(userId))!.Status);
                Assert.IsTrue(await scope.ServiceProvider.GetRequiredService<ISyncQueueRepository>()
                    .HasPendingForDeviceAsync(pair.Source.Identity.LocalDeviceId));
            }
        }
        // Recovery revokes sessions that could still hold the damaged canonical
        // generation. Verify that boundary, then read through a fresh authenticated session.
        await Assert.ThrowsExactlyAsync<InvalidTokenException>(() =>
            pair.Target.Endpoints.GetSavedPasswordsAsync(pair.TargetToken));
        var recoveredTargetToken = await RunStepAsync("target login after canonical recovery", ct =>
            pair.Target.Endpoints.LoginAsync(pair.Target.CreateLoginRequest(username), ct));
        await AssertPasswordSecretAsync(pair.Target, recoveredTargetToken, name, secret);
        Assert.IsFalse((await pair.Source.Endpoints.GetSavedPasswordsAsync(pair.SourceToken))
            .Passwords.Any(item => item.Name == name));
        await pair.Source.Endpoints.LogoutAsync(pair.SourceToken);
        pair.Target.ConnectTo(pair.Source);
        pair.Source.ConnectTo(pair.Target);
        await SynchronizeOneWayUntilAsync(pair.Target, pair.Source, "locked peer stores recovered publication", async () =>
        {
            using var scope = pair.Source.Services.CreateScope();
            var knowledge = await scope.ServiceProvider.GetRequiredService<IUserRevisionKnowledgeRepository>()
                .GetAsync(userId, pair.Target.Identity.LocalDeviceId, pair.Target.Identity.OriginInstanceId, 1);
            return knowledge is not null && knowledge.HighestStoredRevision >= recovered.RecoveryRevision;
        });
        pair.Target.Transport.Disconnect();
        pair.Source.Transport.Disconnect();
        await RunStepAsync("drain sessions before locked restart", async ct =>
        {
            await pair.Source.SyncTasks.StopAllAsync(ct);
            await pair.Target.SyncTasks.StopAllAsync(ct);
        });
        long storedRevision;
        byte[] storedHash;
        using (var scope = pair.Source.Services.CreateScope())
        {
            var stored = await scope.ServiceProvider.GetRequiredService<IUserSyncSnapshotRepository>()
                .GetAsync(userId, pair.Target.Identity.LocalDeviceId, pair.Target.Identity.OriginInstanceId, 1)
                ?? throw new AssertFailedException("The locked recipient did not retain the recovered snapshot.");
            Assert.AreEqual(UserSyncSnapshotStatus.Pending, stored.Status);
            storedRevision = stored.OriginRevision;
            storedHash = stored.SnapshotHash.ToArray();
        }
        var sendsBeforeRestart = pair.Target.Transport.DeltaSendCalls;
        await using var restarted = await pair.Source.RestartAsync();
        Assert.IsTrue(restarted.OwnsPostLoginConsumer,
            "The restarted recipient must own its deferred snapshot-merge consumer.");
        using (var scope = restarted.Services.CreateScope())
        {
            var knowledge = (await scope.ServiceProvider.GetRequiredService<IUserRevisionKnowledgeRepository>()
                .GetAsync(userId, pair.Target.Identity.LocalDeviceId, pair.Target.Identity.OriginInstanceId, 1))!;
            Assert.IsTrue(knowledge.HighestStoredRevision >= recovered.RecoveryRevision);
            var stored = await scope.ServiceProvider.GetRequiredService<IUserSyncSnapshotRepository>()
                .GetAsync(userId, pair.Target.Identity.LocalDeviceId, pair.Target.Identity.OriginInstanceId, 1)
                ?? throw new AssertFailedException("Restart lost the pending recovered snapshot.");
            Assert.AreEqual(UserSyncSnapshotStatus.Pending, stored.Status);
            Assert.AreEqual(storedRevision, stored.OriginRevision);
            CollectionAssert.AreEqual(storedHash, stored.SnapshotHash);
            Assert.IsTrue(knowledge.HighestMergedRevision < recovered.RecoveryRevision,
                "A locked recipient must retain custody without acknowledging a canonical merge.");
        }
        var token = await restarted.Endpoints.LoginAsync(restarted.CreateLoginRequest(username));
        await restarted.Endpoints.GetSavedPasswordsAsync(token);
        await RunStepAsync("unlocked restarted peer merges recovered publication", async ct =>
        {
            while (!(await restarted.Endpoints.GetSavedPasswordsAsync(token, ct)).Passwords.Any(item => item.Name == name))
                await Task.Delay(10, ct);
        });
        await AssertPasswordSecretAsync(restarted, token, name, secret);
        Assert.AreEqual(sendsBeforeRestart, pair.Target.Transport.DeltaSendCalls,
            "The recipient must merge retained custody without another delivery.");
        using var finalScope = restarted.Services.CreateScope();
        var merged = await finalScope.ServiceProvider.GetRequiredService<IUserRevisionKnowledgeRepository>()
            .GetAsync(userId, pair.Target.Identity.LocalDeviceId, pair.Target.Identity.OriginInstanceId, 1)
            ?? throw new AssertFailedException("Merged recovery knowledge was not retained.");
        Assert.IsTrue(merged.HighestMergedRevision >= storedRevision);
    }
}
