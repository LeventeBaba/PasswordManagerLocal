using Microsoft.Data.Sqlite;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using PasswordManagerLocal.Common.Backend.Abstractions.Services;
using PasswordManagerLocal.Common.Backend.Models;
using PasswordManagerLocal.Common.Backend.Persistence;
using PasswordManagerLocal.Common.Backend.Repositories;
using PasswordManagerLocal.Common.Backend.Services;
using PasswordManagerLocal.Common.Backend.Sync;
using PasswordManagerLocal.Common.Backend.Utils;
using PasswordManagerLocal.Common.Contracts.Errors;
using PasswordManagerLocal.Common.Tests.TestInfrastructure;
using PasswordManagerLocal.Common.Tests.TestInfrastructure.Storage;

namespace PasswordManagerLocal.Common.Tests.Backend.Services;

public sealed partial class RevocationReceiptServiceTests
{
    [TestMethod]
    [TestCategory("EvidenceCompaction")]
    [TestCategory("StorageBudget")]
    public async Task EvidenceCompaction_ExactProofCopiesReleaseBytes_WithoutChangingDeliveryOrCustody()
    {
        await using var scenario = await Scenario.CreateAsync(peerCount: 2, authenticatedHistory: true);
        await using var author = await DeliveryNode.CreateAsync(scenario, 0);
        await using var recipient = DeliveryNode.Wrap(scenario);
        await author.ApplyAsync(scenario.Operation); await recipient.ApplyAsync(scenario.Operation);
        await TransferAsync(recipient, author, scenario.Operation.OperationId);
        await TransferAsync(author, recipient, scenario.Operation.OperationId);
        await TransferAsync(recipient, author, scenario.Operation.OperationId);
        Assert.AreEqual(RevocationDeliveryState.Completed, (await SnapshotAsync(author, scenario.Operation.OperationId)).State);
        var evidence = (await author.Db.RevocationReceipts.GetEvidenceAsync(scenario.Operation.OperationId))!;
        Assert.AreEqual(0, UserControlOperationEnvelopeUtil.Deserialize(evidence.EnvelopePayload).RevocationHistoryProofs.Count,
            "New evidence should not admit a redundant signed proof copy.");
        var original = await RestoreDuplicateProofsAsync(author.Db, scenario.Operation);
        var receipts = (await author.Db.RevocationReceipts.ListAsync(scenario.Operation.OperationId)).Select(r => r.Payload.ToArray()).ToArray();
        var before = await SqliteStorageMeasurement.CaptureAsync(author.Db.Connections, "");
        using var session = author.Db.OpenSession();
        var analyzer = new SqliteStorageRetentionAnalyzer(session);
        var finding = (await analyzer.AnalyzeEvidenceAsync(scenario.User.UId)).Single(r =>
            r.TableName == "RevocationReceiptEvidence" && r.RecordId == scenario.Operation.OperationId);
        Assert.AreEqual(StorageRetentionReason.SafeToCompact, finding.Reason);
        Assert.IsTrue(finding.ReclaimableBytes > 0);
        CollectionAssert.AreEqual(original.EnvelopePayload,
            (await new RevocationReceiptRepository(session).GetEvidenceAsync(original.OperationId))!.EnvelopePayload,
            "Analysis must be read-only.");
        var compactor = new SqliteSynchronizationEvidenceCompactor(session, new UserLifecycleCoordinator());
        Assert.AreEqual(finding.ReclaimableBytes, await compactor.CompactAsync(scenario.User.UId));
        Assert.AreEqual(0L, await compactor.CompactAsync(scenario.User.UId));
        var after = await SqliteStorageMeasurement.CaptureAsync(author.Db.Connections, "");
        Assert.AreEqual(before.ChargedBytes["Revocation"] - finding.ReclaimableBytes, after.ChargedBytes["Revocation"]);
        Assert.AreEqual(before.Rows["RevocationReceiptEvidence"], after.Rows["RevocationReceiptEvidence"]);
        Assert.AreEqual(before.Rows["RevocationDeliveryReceipts"], after.Rows["RevocationDeliveryReceipts"]);
        foreach (var category in before.CapacityBytes.Keys) Assert.AreEqual(before.CapacityBytes[category], after.CapacityBytes[category]);
        using var reopened = author.Db.OpenSession();
        var retained = (await new RevocationReceiptRepository(reopened).GetEvidenceAsync(original.OperationId))!;
        CollectionAssert.AreEqual(original.MembershipProofPayload, retained.MembershipProofPayload);
        CollectionAssert.AreEqual(original.AuthorizationPayload, retained.AuthorizationPayload);
        var core = RevocationReceiptService.ReadEvidence(retained);
        CollectionAssert.AreEqual(scenario.Operation.OperationHash, core.Operation.OperationHash);
        CollectionAssert.AreEqual(scenario.Operation.OriginSignature, core.Operation.OriginSignature);
        Assert.AreEqual(1, RevocationDeliveryHistoryUtil.Validate(retained, core.Operation, core.Members).Count(m => m.StartedMembershipEpoch == 1));
        Assert.AreEqual(RevocationDeliveryState.Completed, (await SnapshotAsync(author, original.OperationId)).State);
        Assert.IsNotNull(await author.Db.DeletedUserBarriers.GetAsync(scenario.User.UId));
        var remaining = await new RevocationReceiptRepository(reopened).ListAsync(original.OperationId);
        for (var index = 0; index < receipts.Length; index++) CollectionAssert.AreEqual(receipts[index], remaining[index].Payload);
    }

    [TestMethod]
    [TestCategory("EvidenceCompaction")]
    public async Task EvidenceCompaction_PendingDeliveryAndRelayProofsRemainAvailable()
    {
        await using var scenario = await Scenario.CreateAsync(peerCount: 3, authenticatedHistory: true);
        await using var relay = DeliveryNode.Wrap(scenario);
        await relay.ApplyAsync(scenario.Operation);
        var before = await SnapshotAsync(relay, scenario.Operation.OperationId);
        await RestoreDuplicateProofsAsync(relay.Db, scenario.Operation);
        using var session = relay.Db.OpenSession();
        Assert.IsTrue(await new SqliteSynchronizationEvidenceCompactor(session, scenario.Lifecycle).CompactAsync(scenario.User.UId) > 0);
        var after = await SnapshotAsync(relay, scenario.Operation.OperationId);
        Assert.AreEqual(before.State, after.State);
        Assert.AreEqual(before.AppliedRecipients, after.AppliedRecipients);
        Assert.AreNotEqual(RevocationDeliveryState.Completed, after.State);
        var evidence = (await new RevocationReceiptRepository(session).GetEvidenceAsync(scenario.Operation.OperationId))!;
        var proofs = System.Text.Json.JsonSerializer.Deserialize(evidence.MembershipProofPayload,
            BackendJsonSerializerContext.Default.RevocationHistoryProofEnvelopes)!;
        CollectionAssert.AreEqual(scenario.Operation.RevocationHistoryProofs.Select(p => Convert.ToHexString(p.OriginSignature)).ToArray(),
            proofs.Select(p => Convert.ToHexString(p.OriginSignature)).ToArray());
    }

    [TestMethod]
    [TestCategory("EvidenceCompaction")]
    [DataRow("missing-proof")]
    [DataRow("bad-signature")]
    [DataRow("different-user")]
    [DataRow("conflict")]
    [DataRow("history-reset")]
    public async Task EvidenceCompaction_MissingOrConflictingProofIsRetained(string mutation)
    {
        await using var scenario = await Scenario.CreateAsync(peerCount: 2, authenticatedHistory: true);
        await scenario.Inbox.StoreAndApplyAsync(scenario.Operation, scenario.Peers[0].LocalDeviceId);
        var original = await RestoreDuplicateProofsAsync(scenario.Db, scenario.Operation);
        switch (mutation)
        {
            case "missing-proof": original.MembershipProofPayload = "[]"u8.ToArray(); break;
            case "bad-signature":
                var envelope = UserControlOperationEnvelopeUtil.Deserialize(original.EnvelopePayload);
                envelope.RevocationHistoryProofs.Single().OriginSignature[0] ^= 1;
                original.EnvelopePayload = UserControlOperationEnvelopeUtil.Serialize(envelope);
                break;
            case "different-user": original.UserId = Guid.NewGuid(); break;
            case "conflict": original.HasReceiptConflict = true; break;
            case "history-reset": original.HistoryResetBlocked = true; break;
        }
        await PersistEvidenceAsync(scenario.Db, original);
        using var session = scenario.Db.OpenSession();
        Assert.AreEqual(0L, await new SqliteSynchronizationEvidenceCompactor(session, scenario.Lifecycle).CompactAsync());
        CollectionAssert.AreEqual(original.EnvelopePayload,
            (await new RevocationReceiptRepository(session).GetEvidenceAsync(original.OperationId))!.EnvelopePayload);
        Assert.IsNotNull(await scenario.Db.DeletedUserBarriers.GetAsync(scenario.User.UId));
    }

    [TestMethod]
    [TestCategory("EvidenceCompaction")]
    [DataRow(false)]
    [DataRow(true)]
    public async Task EvidenceCompaction_StateChangesAfterCaptureAreRevalidated(bool cancel)
    {
        await using var scenario = await Scenario.CreateAsync(peerCount: 2, authenticatedHistory: true);
        await scenario.Inbox.StoreAndApplyAsync(scenario.Operation, scenario.Peers[0].LocalDeviceId);
        var original = await RestoreDuplicateProofsAsync(scenario.Db, scenario.Operation);
        using var cancellation = new CancellationTokenSource();
        var lifecycle = new CompactionEntryCoordinator(async () =>
        {
            if (cancel) cancellation.Cancel();
            else
            {
                original.HasReceiptConflict = true;
                await PersistEvidenceAsync(scenario.Db, original);
            }
        });
        using var session = scenario.Db.OpenSession();
        var work = new SqliteSynchronizationEvidenceCompactor(session, lifecycle).CompactAsync(ct: cancellation.Token);
        if (cancel) await Assert.ThrowsAsync<OperationCanceledException>(() => work);
        else Assert.AreEqual(0L, await work);
        CollectionAssert.AreEqual(original.EnvelopePayload,
            (await new RevocationReceiptRepository(session).GetEvidenceAsync(original.OperationId))!.EnvelopePayload);
    }

    [TestMethod]
    [TestCategory("EvidenceCompaction")]
    [TestCategory("StorageBudget")]
    public async Task EvidenceCompaction_FailedWriteRollsBackPayloadAndAccounting()
    {
        await using var scenario = await Scenario.CreateAsync(peerCount: 2, authenticatedHistory: true);
        await scenario.Inbox.StoreAndApplyAsync(scenario.Operation, scenario.Peers[0].LocalDeviceId);
        var original = await RestoreDuplicateProofsAsync(scenario.Db, scenario.Operation);
        var before = await SqliteStorageMeasurement.CaptureAsync(scenario.Db.Connections, "");
        using (var command = await scenario.Db.UnitOfWork.CreateCommandAsync("""
            CREATE TRIGGER RejectCompaction AFTER UPDATE OF EnvelopePayload ON RevocationReceiptEvidence
            BEGIN SELECT RAISE(ABORT, 'injected-compaction-failure'); END;
            """)) await command.ExecuteNonQueryAsync();
        using var session = scenario.Db.OpenSession();
        await Assert.ThrowsExactlyAsync<SqliteException>(() => new SqliteSynchronizationEvidenceCompactor(session, scenario.Lifecycle).CompactAsync());
        CollectionAssert.AreEqual(original.EnvelopePayload,
            (await new RevocationReceiptRepository(session).GetEvidenceAsync(original.OperationId))!.EnvelopePayload);
        var after = await SqliteStorageMeasurement.CaptureAsync(scenario.Db.Connections, "");
        Assert.AreEqual(before.ChargedBytes["Revocation"], after.ChargedBytes["Revocation"]);
        Assert.IsFalse(session.HasPendingChanges);
    }

    [TestMethod]
    [TestCategory("EvidenceCompaction")]
    public async Task EvidenceCompaction_ConcurrentCapturesReleaseEachDuplicateOnlyOnce()
    {
        await using var scenario = await Scenario.CreateAsync(peerCount: 2, authenticatedHistory: true);
        await scenario.Inbox.StoreAndApplyAsync(scenario.Operation, scenario.Peers[0].LocalDeviceId);
        var original = await RestoreDuplicateProofsAsync(scenario.Db, scenario.Operation);
        var expected = RevocationEvidenceCompactionPolicy.Describe(original).ReclaimableBytes;
        var bothCaptured = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var entrants = 0;
        var coordinator = new CompactionEntryCoordinator(async () =>
        {
            if (Interlocked.Increment(ref entrants) == 2) bothCaptured.SetResult();
            await bothCaptured.Task.WaitAsync(TimeSpan.FromSeconds(10));
        });
        using var first = scenario.Db.OpenSession(); using var second = scenario.Db.OpenSession();
        var results = await Task.WhenAll(new SqliteSynchronizationEvidenceCompactor(first, coordinator).CompactAsync(),
            new SqliteSynchronizationEvidenceCompactor(second, coordinator).CompactAsync());
        Assert.AreEqual(expected, results.Sum());
        Assert.AreEqual(1, results.Count(value => value == 0));
        Assert.IsNotNull(await scenario.Db.DeletedUserBarriers.GetAsync(scenario.User.UId));
    }

    [TestMethod]
    [TestCategory("EvidenceCompaction")]
    [TestCategory("StorageBudget")]
    [DataRow(false)]
    [DataRow(true)]
    public async Task EvidenceCompaction_CapacityPressureOnlyAdmitsMoreWorkAfterProvenReclamation(bool missingProof)
    {
        await using var scenario = await Scenario.CreateAsync(peerCount: 2, authenticatedHistory: true);
        await scenario.Inbox.StoreAndApplyAsync(scenario.Operation, scenario.Peers[0].LocalDeviceId);
        var original = await RestoreDuplicateProofsAsync(scenario.Db, scenario.Operation);
        if (missingProof)
        {
            original.MembershipProofPayload = "[]"u8.ToArray();
            await PersistEvidenceAsync(scenario.Db, original);
        }
        await using var connection = scenario.Db.Connections.CreateConnection();
        await connection.OpenAsync();
        var used = await SqliteStorageTestCommands.ScalarAsync(connection,
            "SELECT UsedBytes FROM StorageBudgets WHERE Category='Revocation';");
        await SqliteStorageTestCommands.SetBudgetAsync(connection, "Revocation", used);
        var receipt = (await scenario.Db.RevocationReceipts.ListAsync(original.OperationId)).Single();
        var debt = new RevocationEvidenceDebt
        {
            OperationId = original.OperationId, PeerDeviceId = scenario.Peers[0].LocalDeviceId,
            PeerOriginInstanceId = scenario.Peers[0].OriginInstanceId,
            PeerAuthorizationId = scenario.Members[0].AuthorizationId, ReceiptHash = receipt.ReceiptHash
        };
        async Task AddDebtAsync()
        {
            await using var transaction = await scenario.Db.UnitOfWork.BeginTransactionAsync();
            await scenario.Db.RevocationReceipts.AddDebtAsync(debt);
            await scenario.Db.UnitOfWork.SaveChangesAsync();
            await transaction.CommitAsync();
        }
        await Assert.ThrowsExactlyAsync<StorageCapacityExceededException>(AddDebtAsync);
        using var session = scenario.Db.OpenSession();
        var released = await new SqliteSynchronizationEvidenceCompactor(session, scenario.Lifecycle).CompactAsync();
        if (missingProof)
        {
            Assert.AreEqual(0L, released);
            await Assert.ThrowsExactlyAsync<StorageCapacityExceededException>(AddDebtAsync);
        }
        else
        {
            Assert.IsTrue(released > 0);
            await AddDebtAsync();
        }
        Assert.AreEqual(used, await SqliteStorageTestCommands.ScalarAsync(connection,
            "SELECT LimitBytes FROM StorageBudgets WHERE Category='Revocation';"));
        Assert.IsNotNull(await scenario.Db.DeletedUserBarriers.GetAsync(scenario.User.UId));
    }

    [TestMethod]
    [TestCategory("EvidenceCompaction")]
    [TestCategory("BoundedGrowth")]
    [TestCategory("SyncStressExtended")]
    public async Task EvidenceCompaction_RepeatedDuplicateArrivalsAndUnchangedMaintenanceHaveStableCharges()
    {
        if (Environment.GetEnvironmentVariable("PASSWORDMANAGERLOCAL_SYNC_STRESS") != "1")
            Assert.Inconclusive("Set PASSWORDMANAGERLOCAL_SYNC_STRESS=1 to run evidence churn.");
        // This storage stress case needs one persistent image across every session.
        // Use the existing bounded file fixture rather than named shared-memory cache identity.
        await using var scenario = await Scenario.CreateAsync(peerCount: 3, authenticatedHistory: true, fileBacked: true);
        Assert.IsTrue(File.Exists(scenario.Db.DatabasePath));
        using (var independent = scenario.Db.OpenSession())
        {
            Assert.IsNotNull(await new UserRepository(independent).GetByIdAsNoTrackingAsync(scenario.User.UId));
            Assert.IsFalse(await new DeletedUserBarrierRepository(independent).ExistsAsync(scenario.User.UId));
        }
        await scenario.Inbox.StoreAndApplyAsync(scenario.Operation, scenario.Peers[0].LocalDeviceId);
        var baseline = await SqliteStorageMeasurement.CaptureAsync(scenario.Db.Connections, scenario.Db.DatabasePath);
        for (var round = 0; round < 64; round++)
        {
            await RestoreDuplicateProofsAsync(scenario.Db, scenario.Operation);
            using var session = scenario.Db.OpenSession();
            var compactor = new SqliteSynchronizationEvidenceCompactor(session, scenario.Lifecycle);
            Assert.IsTrue(await compactor.CompactAsync() > 0, $"round={round}");
            Assert.AreEqual(0L, await compactor.CompactAsync());
            var current = await SqliteStorageMeasurement.CaptureAsync(scenario.Db.Connections, scenario.Db.DatabasePath);
            foreach (var category in baseline.ChargedBytes.Keys)
                Assert.AreEqual(baseline.ChargedBytes[category], current.ChargedBytes[category], $"category={category}, round={round}");
            foreach (var table in baseline.Rows.Keys)
                Assert.AreEqual(baseline.Rows[table], current.Rows[table], $"table={table}, round={round}");
        }
    }

    private static async Task<RevocationReceiptEvidence> RestoreDuplicateProofsAsync(
        SqliteRepositoryTestDatabase database, UserControlOperationEnvelope operation)
    {
        var evidence = (await database.RevocationReceipts.GetEvidenceAsync(operation.OperationId))!;
        evidence.EnvelopePayload = UserControlOperationEnvelopeUtil.Serialize(operation);
        await PersistEvidenceAsync(database, evidence);
        return evidence;
    }

    private static async Task PersistEvidenceAsync(SqliteRepositoryTestDatabase database, RevocationReceiptEvidence evidence)
    {
        await using var transaction = await database.UnitOfWork.BeginTransactionAsync();
        database.RevocationReceipts.UpdateEvidence(evidence);
        await database.UnitOfWork.SaveChangesAsync();
        await transaction.CommitAsync();
    }

    private sealed class CompactionEntryCoordinator(Func<Task> beforeEntry) : IUserLifecycleCoordinator
    {
        private readonly UserLifecycleCoordinator _inner = new();
        public async Task<T> ExecuteAsync<T>(Guid userId, Func<CancellationToken, Task<T>> action, CancellationToken ct = default)
        {
            await beforeEntry();
            return await _inner.ExecuteAsync(userId, action, ct);
        }
        public Task ExecuteAsync(Guid userId, Func<CancellationToken, Task> action, CancellationToken ct = default) =>
            ExecuteAsync<object?>(userId, async token => { await action(token); return null; }, ct);
    }
}
