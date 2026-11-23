using Microsoft.Data.Sqlite;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using PasswordManagerLocal.Common.Backend.Abstractions.Persistence;
using PasswordManagerLocal.Common.Backend.Abstractions.Repositories;
using PasswordManagerLocal.Common.Backend.Abstractions.Services;
using PasswordManagerLocal.Common.Backend.Models;
using PasswordManagerLocal.Common.Backend.Persistence;
using PasswordManagerLocal.Common.Backend.Security;
using PasswordManagerLocal.Common.Backend.Services;
using PasswordManagerLocal.Common.Backend.Sync;
using PasswordManagerLocal.Common.Contracts.Errors;
using PasswordManagerLocal.Common.Tests.TestInfrastructure.Storage;

namespace PasswordManagerLocal.Common.Tests.Backend.Services;

public sealed partial class UserTombstoneGarbageCollectorIntegrationTests
{
    [TestMethod]
    [TestCategory("Backend")]
    [TestCategory("CausalGC")]
    public async Task SqliteGc_PartialCollectionPublishesCheckpointAndPreservesExactPendingEvidenceAcrossReopen()
    {
        await using var scenario = await PendingCausalScenario.CreateSqliteAsync();
        var services = scenario.Services;
        var result = await services.GetRequiredService<IUserTombstoneGarbageCollector>().CollectAsync(scenario.UserId, scenario.Key);
        Assert.AreEqual(1, result.RemovedCount); Assert.AreEqual(1, result.RetainedCount);
        await using var reopened = scenario.SqliteHost.Services.CreateAsyncScope();
        var user = (await reopened.ServiceProvider.GetRequiredService<IUserRepository>().GetByIdWithRelationsAsync(scenario.UserId))!;
        using var bundle = await reopened.ServiceProvider.GetRequiredService<IUserDataReaderService>().GetAndVerifyUserDataBundleAsync(user, scenario.Key);
        Assert.AreEqual(scenario.OlderDeletionId, bundle.UserPasswordsData.DeletedPasswords.Single().Id);
        var pending = (await reopened.ServiceProvider.GetRequiredService<IUserSyncSnapshotRepository>().GetExactAsync(
            scenario.UserId, scenario.PendingRow.OriginDeviceId, scenario.PendingRow.OriginInstanceId, 1, 1))!;
        Assert.AreEqual(UserSyncSnapshotStatus.Pending, pending.Status);
        CollectionAssert.AreEqual(scenario.PendingRow.EnvelopePayload, pending.EnvelopePayload);
        var health = await reopened.ServiceProvider.GetRequiredService<IUserCanonicalHealthService>().VerifyAsync(
            user, scenario.Key, UserSyncKeyConfidence.ExplicitlyTrusted, recordFault: false);
        Assert.IsTrue(health.FullyVerified);
        Assert.IsFalse((await reopened.ServiceProvider.GetRequiredService<IUserTombstoneGarbageCollector>()
            .CollectAsync(scenario.UserId, scenario.Key)).Changed);
    }

    [TestMethod]
    [TestCategory("Backend")]
    [TestCategory("CausalGC")]
    public async Task SqliteGc_BackendRestartRetainsTheSignedCheckpointAndUnresolvedOriginIdentity()
    {
        await using var scenario = await PendingCausalScenario.CreateSqliteAsync();
        Assert.AreEqual(1, (await scenario.Services.GetRequiredService<IUserTombstoneGarbageCollector>()
            .CollectAsync(scenario.UserId, scenario.Key)).RemovedCount);
        await scenario.CloseScopeForRestartAsync();
        await using var restarted = await scenario.SqliteHost.RestartAsync();
        await using var scope = restarted.Services.CreateAsyncScope();
        var services = scope.ServiceProvider;
        var user = (await services.GetRequiredService<IUserRepository>().GetByIdAsNoTrackingAsync(scenario.UserId))!;
        var health = await services.GetRequiredService<IUserCanonicalHealthService>().VerifyAsync(user, scenario.Key,
            UserSyncKeyConfidence.ExplicitlyTrusted, recordFault: false);
        Assert.IsTrue(health.FullyVerified);
        var pending = (await services.GetRequiredService<IUserSyncSnapshotRepository>().GetExactAsync(scenario.UserId,
            scenario.PendingRow.OriginDeviceId, scenario.PendingRow.OriginInstanceId, 1, 1))!;
        Assert.AreEqual(UserSyncSnapshotStatus.Pending, pending.Status);
        CollectionAssert.AreEqual(scenario.PendingRow.SnapshotHash, pending.SnapshotHash);
    }

    [TestMethod]
    [TestCategory("Backend")]
    [TestCategory("CausalGC")]
    [TestCategory("StorageBudget")]
    [DataRow("cancel")]
    [DataRow("write")]
    [DataRow("capacity")]
    public async Task SqliteGc_FailureAfterCanonicalFlushRollsBackPublicationCheckpointKnowledgeAndAccounting(string failure)
    {
        await using var scenario = await PendingCausalScenario.CreateSqliteAsync();
        var services = scenario.Services;
        var old = (await services.GetRequiredService<IUserRepository>().GetByIdAsNoTrackingAsync(scenario.UserId))!;
        // The independent monotonic clock may durably observe verified data outside a
        // user transaction. Establish that floor before measuring rollback accounting.
        using (await services.GetRequiredService<IUserDataReaderService>().GetAndVerifyUserDataBundleAsync(old, scenario.Key)) { }
        var before = await SqliteStorageMeasurement.CaptureAsync(scenario.SqliteHost);
        using var cancellation = new CancellationTokenSource();
        var publisher = services.GetRequiredService<IUserSnapshotPublisherService>();
        if (failure == "cancel") publisher = new PublicationBoundaryPublisher(publisher, () =>
        { cancellation.Cancel(); return Task.CompletedTask; });
        else
        {
            var message = failure == "capacity" ? "pml_capacity:injected-gc-publication" : "injected-gc-publication";
            var session = services.GetRequiredService<SqliteSession>();
            using var command = await session.CreateCommandAsync("CREATE TRIGGER RejectGcPublication BEFORE UPDATE ON UserSyncSnapshots " +
                "BEGIN SELECT RAISE(ABORT, '" + message + "'); END;");
            await command.ExecuteNonQueryAsync();
        }
        var work = CreateCollector(services, publisher).CollectAsync(scenario.UserId, scenario.Key, cancellation.Token);
        if (failure == "cancel") await Assert.ThrowsAsync<OperationCanceledException>(() => work);
        else if (failure == "capacity") await Assert.ThrowsExactlyAsync<StorageCapacityExceededException>(() => work);
        else await Assert.ThrowsExactlyAsync<SqliteException>(() => work);
        await using var reopened = scenario.SqliteHost.Services.CreateAsyncScope();
        var afterUser = (await reopened.ServiceProvider.GetRequiredService<IUserRepository>().GetByIdWithRelationsAsync(scenario.UserId))!;
        CollectionAssert.AreEqual(old.EncryptedUserPasswordsDataPayload, afterUser.EncryptedUserPasswordsDataPayload);
        CollectionAssert.AreEqual(old.IntegrityHash, afterUser.IntegrityHash);
        using var bundle = await reopened.ServiceProvider.GetRequiredService<IUserDataReaderService>().GetAndVerifyUserDataBundleAsync(afterUser, scenario.Key);
        Assert.AreEqual(2, bundle.UserPasswordsData.DeletedPasswords.Count);
        var after = await SqliteStorageMeasurement.CaptureAsync(scenario.SqliteHost);
        foreach (var table in before.Rows.Keys) Assert.AreEqual(before.Rows[table], after.Rows[table], table);
        foreach (var category in before.ChargedBytes.Keys) Assert.AreEqual(before.ChargedBytes[category], after.ChargedBytes[category], category);
        foreach (var table in before.BlobBytes.Keys) Assert.AreEqual(before.BlobBytes[table], after.BlobBytes[table], table);
    }

    [TestMethod]
    [TestCategory("Backend")]
    [TestCategory("CausalGC")]
    [DataRow("snapshot")]
    [DataRow("compaction")]
    [DataRow("account-deletion")]
    [DataRow("key-epoch")]
    [DataRow("device-removal")]
    [DataRow("recovery")]
    public async Task SqliteGc_LifecycleBoundarySerializesConcurrentEvidenceAndAuthorityChanges(string operation)
    {
        await using var scenario = await PendingCausalScenario.CreateSqliteAsync();
        var services = scenario.Services;
        if (operation == "compaction") await SeedRecoveredPeerFaultAsync(scenario);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var publisher = new PublicationBoundaryPublisher(services.GetRequiredService<IUserSnapshotPublisherService>(), async () =>
        { entered.TrySetResult(); await release.Task; });
        var gc = Task.Run(() => CreateCollector(services, publisher).CollectAsync(scenario.UserId, scenario.Key));
        Task? concurrent = null;
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(15));
            var attempted = scenario.ObserveNextLifecycleAttempt();
            concurrent = Task.Run(async () =>
            {
                await using var scope = scenario.SqliteHost.Services.CreateAsyncScope();
                var peerServices = scope.ServiceProvider;
                if (operation == "snapshot")
                {
                    var replay = UserSnapshotEvidenceUtil.DeserializeExact(scenario.LaterAnchorRow);
                    var receipt = await peerServices.GetRequiredService<IUserSnapshotInboxService>().StoreAsync(replay, Guid.NewGuid());
                    Assert.AreEqual(UserSnapshotReceiptState.ObsoleteRevision, receipt.State);
                }
                else if (operation == "compaction")
                    Assert.IsTrue(await peerServices.GetRequiredService<SqliteSynchronizationEvidenceCompactor>().CompactAsync(scenario.UserId) > 0);
                else if (operation == "recovery")
                {
                    var recovered = await peerServices.GetRequiredService<IUserDataRecoveryCoordinator>().TryRecoverAsync(scenario.UserId,
                        scenario.Key, UserSyncKeyConfidence.ExplicitlyTrusted, UserDataRecoveryTrigger.ManualRetry);
                    Assert.AreEqual(UserDataRecoveryState.NothingToRecover, recovered.State);
                }
                else await ApplyConcurrentControlAsync(scenario, peerServices, operation);
            });
            await attempted.WaitAsync(TimeSpan.FromSeconds(15));
            Assert.IsFalse(concurrent.IsCompleted);
            release.TrySetResult();
            Assert.AreEqual(1, (await gc.WaitAsync(TimeSpan.FromSeconds(30))).RemovedCount);
            await concurrent.WaitAsync(TimeSpan.FromSeconds(30));
        }
        finally
        {
            release.TrySetResult();
            // Join both owners before closing their SQLite sessions and deleting fixture files.
            try { await gc.WaitAsync(TimeSpan.FromSeconds(30)); }
            finally { if (concurrent is not null) await concurrent.WaitAsync(TimeSpan.FromSeconds(30)); }
        }
        await using var readback = scenario.SqliteHost.Services.CreateAsyncScope();
        var finalServices = readback.ServiceProvider;
        var user = await finalServices.GetRequiredService<IUserRepository>().GetByIdAsNoTrackingAsync(scenario.UserId);
        if (operation == "account-deletion")
        {
            Assert.IsNull(user); Assert.IsNotNull(await finalServices.GetRequiredService<IDeletedUserBarrierRepository>().GetAsync(scenario.UserId));
        }
        else
        {
            Assert.IsNotNull(user);
            Assert.AreEqual(operation == "key-epoch" ? 3L : 2L, user.KeyEpoch);
            Assert.AreEqual(operation == "device-removal" ? 3L : 2L, user.MembershipEpoch);
            Assert.IsFalse((await finalServices.GetRequiredService<IUserControlStateRepository>().GetAsync(scenario.UserId))!.HasConflict);
        }
    }

    [TestMethod]
    [TestCategory("Backend")]
    [TestCategory("CausalGC")]
    [TestCategory("EvidenceCompaction")]
    public async Task SqliteGc_FaultCompactionKeepsExactHealthySourcesForCanonicalReconstruction()
    {
        await using var scenario = await PendingCausalScenario.CreateSqliteAsync();
        var services = scenario.Services;
        await SeedRecoveredPeerFaultAsync(scenario);
        var snapshots = services.GetRequiredService<IUserSyncSnapshotRepository>();
        var source = (await snapshots.GetAsync(scenario.UserId, scenario.PendingRow.OriginDeviceId,
            scenario.PendingRow.OriginInstanceId, 2))!;
        Assert.IsTrue(await services.GetRequiredService<SqliteSynchronizationEvidenceCompactor>().CompactAsync(scenario.UserId) > 0);
        CollectionAssert.AreEqual(source.EnvelopePayload, (await snapshots.GetAsync(scenario.UserId, source.OriginDeviceId,
            source.OriginInstanceId, 2))!.EnvelopePayload);
        var user = (await services.GetRequiredService<IUserRepository>().GetByIdAsNoTrackingAsync(scenario.UserId))!;
        var corrupt = user.EncryptedPayload.ToArray(); corrupt[0] ^= 0x5A;
        using (var command = await services.GetRequiredService<SqliteSession>().CreateCommandAsync(
            "UPDATE Users SET EncryptedPayload = $payload WHERE UId = $user;"))
        {
            command.Parameters.AddWithValue("$payload", corrupt); command.Parameters.AddWithValue("$user", scenario.UserId);
            await command.ExecuteNonQueryAsync();
        }
        var recovery = await services.GetRequiredService<IUserDataRecoveryCoordinator>().TryRecoverAsync(scenario.UserId,
            scenario.Key, UserSyncKeyConfidence.ExplicitlyTrusted, UserDataRecoveryTrigger.ManualRetry);
        Assert.AreEqual(UserDataRecoveryState.Recovered, recovery.State);
        await using var reopened = scenario.SqliteHost.Services.CreateAsyncScope();
        var restored = (await reopened.ServiceProvider.GetRequiredService<IUserRepository>().GetByIdAsNoTrackingAsync(scenario.UserId))!;
        var health = await reopened.ServiceProvider.GetRequiredService<IUserCanonicalHealthService>().VerifyAsync(restored,
            scenario.Key, UserSyncKeyConfidence.ExplicitlyTrusted, recordFault: false);
        Assert.IsTrue(health.FullyVerified);
        Assert.IsNotNull(await reopened.ServiceProvider.GetRequiredService<IUserSyncSnapshotRepository>().GetExactAsync(
            scenario.UserId, source.OriginDeviceId, source.OriginInstanceId, source.UserKeyEpoch, source.OriginRevision));
    }

    private static async Task SeedRecoveredPeerFaultAsync(PendingCausalScenario scenario)
    {
        var services = scenario.Services;
        var faults = services.GetRequiredService<IUserSyncFaultService>();
        await faults.RecordAsync(new UserSyncFaultDescriptor
        {
            UserId = scenario.UserId, Scope = UserSyncFaultScope.SnapshotOrigin, Kind = UserSyncFaultKind.IncomingIntegrityFailure,
            Status = UserSyncHealthStatus.Isolated, OriginDeviceId = scenario.PendingRow.OriginDeviceId,
            OriginInstanceId = scenario.PendingRow.OriginInstanceId, KeyEpoch = 2, MembershipEpoch = 2, OriginRevision = 1,
            ObservedHash = Hashing.SHA256Hash([99]), AffectedComponent = "Passwords", DiagnosticCode = "recovered-before-gc"
        });
        await services.GetRequiredService<IUnitOfWork>().SaveChangesAsync();
        await faults.MarkOriginRecoveredAsync(scenario.UserId, scenario.PendingRow.OriginDeviceId,
            scenario.PendingRow.OriginInstanceId, 2, 2);
        await services.GetRequiredService<IUnitOfWork>().SaveChangesAsync();
    }

    private static async Task ApplyConcurrentControlAsync(PendingCausalScenario scenario, IServiceProvider services, string operation)
    {
        var lifecycle = services.GetRequiredService<IUserLifecycleCoordinator>();
        // Prepare against the committed post-GC canonical generation while holding the same
        // user lock. This control operation is concurrent with GC but must not use stale bytes.
        await lifecycle.ExecuteControlAsync(scenario.UserId, async ct =>
        {
            var user = (await services.GetRequiredService<IUserRepository>().GetByIdAsNoTrackingAsync(scenario.UserId, ct))!;
            var members = await services.GetRequiredService<IUserMembershipAuthorizationRepository>().ListForUserAsync(scenario.UserId, ct);
            var envelope = new UserControlOperationEnvelope
            {
                OperationId = Guid.NewGuid(), UserId = scenario.UserId, OriginDeviceId = scenario.PendingRow.OriginDeviceId,
                OriginInstanceId = scenario.PendingRow.OriginInstanceId, OriginSequence = 1, CreatedAtUtc = DateTimeOffset.UtcNow,
                PreviousKeyEpoch = 2, ResultingKeyEpoch = 2, PreviousMembershipEpoch = 2, ResultingMembershipEpoch = 2
            };
            if (operation == "key-epoch")
            {
                user.KeyEpoch = 3; user.GenerateIntegrityHash();
                envelope.OperationType = UserControlOperationType.KeyEpochReplacement; envelope.ResultingKeyEpoch = 3;
                envelope.OperationPayload = UserControlOperationEnvelopeUtil.SerializeKeyEpochReplacementPayload(
                    UserControlOperationEnvelopeUtil.CreateKeyEpochReplacementPayload(user, 2));
            }
            else
            {
                envelope.RevocationMembers = UserControlOperationEnvelopeUtil.CreateMembershipSummary(members);
                envelope.RevocationHistoryProofs = (await services.GetRequiredService<IUserControlOperationRepository>().ListForUserAsync(scenario.UserId, ct))
                    .Where(row => row.OperationType == UserControlOperationType.DeviceAddition)
                    .Select(row => UserControlOperationEnvelopeUtil.CopyCoreProof(UserControlOperationEnvelopeUtil.Deserialize(row.EnvelopePayload))).ToList();
                if (operation == "account-deletion")
                {
                    envelope.OperationType = UserControlOperationType.AccountDeletion;
                    envelope.OperationPayload = UserControlOperationEnvelopeUtil.SerializeAccountDeletionPayload(
                        UserControlOperationEnvelopeUtil.CreateAccountDeletionPayload(user, members));
                }
                else
                {
                    envelope.OperationType = UserControlOperationType.DeviceRemoval; envelope.ResultingMembershipEpoch = 3;
                    var member = members.Single(row => row.DeviceId == scenario.PendingRow.OriginDeviceId);
                    var payload = new DeviceRemovalPayload
                    {
                        UserId = scenario.UserId, RemovedDeviceId = member.DeviceId, KeyEpoch = 2,
                        PreviousMembershipEpoch = 2, ResultingMembershipEpoch = 3,
                        Origins = Enumerable.Range(1, 2).Select(epoch => new DeviceRemovalOriginCutoffPayload
                        {
                            AuthorizationId = member.AuthorizationId, OriginInstanceId = member.OriginInstanceId, UserKeyEpoch = epoch,
                            HighestAcceptedSnapshotRevision = epoch, HighestAcceptedControlSequence = 1,
                            SignPublicKeyHash = member.SignPublicKeyHash, AdditionOperationId = member.AdditionOperationId,
                            AdditionOperationHash = member.AdditionOperationHash
                        }).ToList()
                    };
                    UserControlOperationEnvelopeUtil.FinalizeDeviceRemovalPayload(payload);
                    envelope.OperationPayload = UserControlOperationEnvelopeUtil.SerializeDeviceRemovalPayload(payload);
                }
            }
            scenario.SignPeerControl(envelope);
            var result = await services.GetRequiredService<IUserControlOperationInboxService>().StoreAndApplyAsync(envelope, Guid.NewGuid(), ct);
            Assert.AreEqual(UserControlOperationReceiptState.Applied, result.State);
            return true;
        });
    }

    private sealed class PublicationBoundaryPublisher(IUserSnapshotPublisherService inner, Func<Task> afterFlush) : IUserSnapshotPublisherService
    {
        public async Task<UserSyncSnapshot> GetOrCreateAsync(User user, CancellationToken ct = default)
        {
            var snapshot = await inner.GetOrCreateAsync(user, ct); await afterFlush(); return snapshot;
        }
        public Task<UserSyncSnapshot?> GetLatestAsync(Guid userId, long userKeyEpoch, CancellationToken ct = default) => inner.GetLatestAsync(userId, userKeyEpoch, ct);
        public Task<UserSyncSnapshot> GetOrCreateAfterRecoveryAsync(User user, EncryptionKey key, UserSyncKeyConfidence confidence, CancellationToken ct = default) =>
            inner.GetOrCreateAfterRecoveryAsync(user, key, confidence, ct);
    }
}
