using Microsoft.Extensions.DependencyInjection;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using PasswordManagerLocal.Common.Backend.Abstractions.Repositories;
using PasswordManagerLocal.Common.Backend.Abstractions.Services;
using PasswordManagerLocal.Common.Backend.Sync;
using PasswordManagerLocal.Common.Backend.Sync.Discovery;
using PasswordManagerLocal.Common.Contracts.Requests;
using PasswordManagerLocal.Common.Tests.TestInfrastructure;
using PasswordManagerLocal.Common.Tests.TestInfrastructure.Storage;
using System.Text;
using System.Text.Json;
using PasswordManagerLocal.Common.Backend.Utils;

namespace PasswordManagerLocal.Common.Tests.Backend.Services;

public sealed partial class RegularSyncEndToEndTests
{
    [TestMethod]
    [TestCategory("Backend")]
    [TestCategory("Integration")]
    [Timeout(TestTimeoutMilliseconds)]
    [TestCategory("DatabaseGrowthBaseline")]
    [TestCategory("Security")]
    public async Task RepeatedMalformedSnapshot_RejectionDoesNotPersistPayloadOrReplaceCheckpoint()
    {
        await using var pair = await CreateEnrolledPairAsync();
        await PauseAutomaticDeliveryAsync(pair);
        UserSnapshotEnvelope malformed;
        using (var scope = pair.Source.Services.CreateScope())
        {
            var user = (await scope.ServiceProvider.GetRequiredService<IUserRepository>()
                .GetByIdWithRelationsAsync((await pair.Source.GetOnlyLoginIdentityAsync()).UserId))!;
            var row = await scope.ServiceProvider.GetRequiredService<IUserSnapshotPublisherService>().GetOrCreateAsync(user);
            malformed = JsonSerializer.Deserialize(row.EnvelopePayload, BackendJsonSerializerContext.Default.UserSnapshotEnvelope)!;
        }
        malformed.SnapshotHash[0] ^= 1;
        var lifecycle = pair.Target.Services.GetRequiredService<IUserLifecycleCoordinator>();
        using var cancellation = new CancellationTokenSource(StepTimeout);
        // Releasing the initial-read gate does not join deferred post-login reconciliation.
        // Take the same per-user ownership as that worker before capturing the baseline;
        // nested inbox admissions remain reentrant and execute their real transactions.
        await lifecycle.ExecuteAsync(malformed.UserId, async ct =>
        {
            var before = await SqliteStorageMeasurement.CaptureAsync(pair.Target, ct);
            before.WriteTo(TestContext, "malformed-envelope-before");
            var checkpoint = await pair.Target.GetOnlyCanonicalCheckpointAsync(ct);
            Assert.IsNotNull(checkpoint);
            var snapshots = await BaselineSnapshotIdentitiesAsync(pair.Target);
            for (var index = 0; index < 16; index++)
            {
                using var scope = pair.Target.Services.CreateScope();
                await Assert.ThrowsExactlyAsync<InvalidDataException>(() => scope.ServiceProvider.GetRequiredService<IUserSnapshotInboxService>()
                    .StoreAsync(malformed, pair.Source.Identity.LocalDeviceId, ct));
            }
            var after = await SqliteStorageMeasurement.CaptureAsync(pair.Target, ct);
            after.WriteTo(TestContext, "malformed-envelope-after-16");
            before.AssertSameRows(after);
            var persisted = await pair.Target.GetOnlyCanonicalCheckpointAsync(ct);
            Assert.IsNotNull(persisted);
            CollectionAssert.AreEqual(checkpoint.CanonicalContentHash, persisted.CanonicalContentHash,
                "Malformed snapshot admission must not change canonical content.");
            Assert.AreEqual(checkpoint.CheckpointSequence, persisted.CheckpointSequence,
                "Malformed snapshot admission must not replace the checkpoint generation.");
            CollectionAssert.AreEqual(checkpoint.Signature, persisted.Signature,
                "Malformed snapshot admission must preserve the signed checkpoint.");
            CollectionAssert.AreEqual(snapshots, await BaselineSnapshotIdentitiesAsync(pair.Target),
                "Malformed snapshot admission must not replace persisted snapshot identities.");
        }, cancellation.Token);
    }

    [TestMethod]
    [TestCategory("Backend")]
    [TestCategory("Integration")]
    [Timeout(TestTimeoutMilliseconds)]
    [TestCategory("DatabaseGrowthBaseline")]
    public async Task RepeatedNoOpSyncAndControlAntiEntropy_DoNotAccumulateEvidenceOrRevisions()
    {
        await using var pair = await CreateEnrolledPairAsync();
        await PauseAutomaticDeliveryAsync(pair);
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        for (var index = 0; index < 4; index++)
        {
            await StartSyncAsync(pair.Source, pair.Target, "baseline warmup forward", cancellation.Token);
            await StartSyncAsync(pair.Target, pair.Source, "baseline warmup reverse", cancellation.Token);
        }
        var before = await SqliteStorageMeasurement.CaptureAsync(pair.Source);
        before.WriteTo(TestContext, "no-op-sync-before");
        var revisions = await BaselineSnapshotIdentitiesAsync(pair.Source);
        var inventoryCalls = pair.Source.Transport.ControlOperationInventoryExchangeCalls;
        for (var index = 0; index < 12; index++)
        {
            await StartSyncAsync(pair.Source, pair.Target, "baseline no-op forward", cancellation.Token);
            await StartSyncAsync(pair.Target, pair.Source, "baseline no-op reverse", cancellation.Token);
        }
        Assert.IsTrue(pair.Source.Transport.ControlOperationInventoryExchangeCalls >= inventoryCalls + 12,
            "The workload must actually execute control anti-entropy.");
        var after = await SqliteStorageMeasurement.CaptureAsync(pair.Source);
        after.WriteTo(TestContext, "no-op-sync-after-12");
        before.AssertSameRows(after);
        CollectionAssert.AreEqual(revisions, await BaselineSnapshotIdentitiesAsync(pair.Source),
            "No-op synchronization must preserve signed origin revisions and hashes.");
        using (var scope = pair.Source.Services.CreateScope())
        {
            var device = (await scope.ServiceProvider.GetRequiredService<IDeviceRepository>()
                .GetByIdWithUserDevicesAsync(pair.Target.Identity.LocalDeviceId))!;
            var endpoint = new DiscoveredDeviceEndpoint { Host = "127.0.0.1", Port = 26688,
                TlsCertFingerprint = pair.Target.Identity.FingerprintHex };
            var controlCalls = pair.Source.Transport.ControlOperationInventoryExchangeCalls;
            for (var index = 0; index < 48; index++)
            {
                pair.Source.SyncTasks.TryStartFromDiscovery(endpoint, device);
                await pair.Source.WaitForSyncIdleAsync(device.Id, cancellation.Token);
            }
            Assert.AreEqual(controlCalls, pair.Source.Transport.ControlOperationInventoryExchangeCalls,
                "Unchanged discovery within the reconciliation window must not reopen control sessions.");
            var discovery = await SqliteStorageMeasurement.CaptureAsync(pair.Source);
            discovery.WriteTo(TestContext, "duplicate-discovery-after-48");
            after.AssertSameRows(discovery);
        }
        await SqliteStorageMeasurement.CheckpointAsync(pair.Source);
        (await SqliteStorageMeasurement.CaptureAsync(pair.Source)).WriteTo(TestContext, "no-op-sync-checkpointed");
    }

    [TestMethod]
    [TestCategory("Backend")]
    [TestCategory("Integration")]
    [Timeout(ThreeDeviceTestTimeoutMilliseconds)]
    [TestCategory("DatabaseGrowthBaseline")]
    public async Task RepeatedEditsAndRemoteMerges_WithOneLivePassword_RecordBothVaults()
    {
        await using var pair = await CreateEnrolledPairAsync();
        await PauseAutomaticDeliveryAsync(pair);
        await AddPasswordAsync(pair.Source, pair.SourceToken, "growth-shared-entry", Encoding.UTF8.GetBytes("baseline-merge-secret"));
        await SynchronizeUntilAsync(pair, "baseline password reaches target", async () =>
            (await pair.Target.Endpoints.GetSavedPasswordsAsync(pair.TargetToken)).Passwords.Count == 1);
        var id = (await pair.Source.Endpoints.GetSavedPasswordsAsync(pair.SourceToken)).Passwords.Single().Id;
        for (var round = 0; round < 8; round++)
        {
            var writer = round % 2 == 0 ? pair.Source : pair.Target;
            var reader = round % 2 == 0 ? pair.Target : pair.Source;
            var writerToken = round % 2 == 0 ? pair.SourceToken : pair.TargetToken;
            var readerToken = round % 2 == 0 ? pair.TargetToken : pair.SourceToken;
            var description = $"baseline-edit-{round}";
            await writer.Endpoints.UpdatePasswordAsync(writerToken, new UpdatePasswordRequest { Id = id, Description = description });
            await SynchronizeUntilAsync(pair, "bounded-live-content edit converges", async () =>
                (await reader.Endpoints.GetSavedPasswordsAsync(readerToken)).Passwords.Single().Description == description);
            foreach (var host in new[] { pair.Source, pair.Target })
            {
                var measurement = await SqliteStorageMeasurement.CaptureAsync(host);
                measurement.WriteTo(TestContext, $"merge-round-{round + 1}-{(ReferenceEquals(host, pair.Source) ? "source" : "target")}");
                Assert.AreEqual(1L, measurement.Rows["Users"]);
                Assert.IsTrue(measurement.Rows["UserSyncSnapshots"] <= 2, "Latest snapshots are bounded by the two fixed origins in this workload.");
            }
        }
        Assert.AreEqual(1, (await pair.Source.Endpoints.GetSavedPasswordsAsync(pair.SourceToken)).Passwords.Count);
        Assert.AreEqual(1, (await pair.Target.Endpoints.GetSavedPasswordsAsync(pair.TargetToken)).Passwords.Count);
    }

    private static async Task<string[]> BaselineSnapshotIdentitiesAsync(ProductionSyncTestHost host)
    {
        using var scope = host.Services.CreateScope();
        var userId = (await host.GetOnlyLoginIdentityAsync()).UserId;
        var rows = await scope.ServiceProvider.GetRequiredService<IUserSyncSnapshotRepository>().ListForUserAsync(userId);
        return rows.Select(row => $"{row.OriginDeviceId:N}/{row.OriginInstanceId:N}/{row.UserKeyEpoch}/{row.OriginRevision}/{Convert.ToHexString(row.SnapshotHash)}")
            .Order(StringComparer.Ordinal).ToArray();
    }
}
