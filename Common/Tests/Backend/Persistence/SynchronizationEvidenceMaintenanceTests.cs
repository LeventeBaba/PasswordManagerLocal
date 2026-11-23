using Microsoft.VisualStudio.TestTools.UnitTesting;
using PasswordManagerLocal.Common.Backend.Models;
using PasswordManagerLocal.Common.Backend.Persistence;
using PasswordManagerLocal.Common.Backend.Repositories;
using PasswordManagerLocal.Common.Tests.TestInfrastructure;
using static PasswordManagerLocal.Common.Tests.TestInfrastructure.Storage.SqliteStorageTestCommands;

namespace PasswordManagerLocal.Common.Tests.Backend.Persistence;

[TestClass]
[TestCategory("Backend")]
[TestCategory("EvidenceCompaction")]
public sealed class SynchronizationEvidenceMaintenanceTests
{
    [TestMethod]
    public void RequestsCoalesce_ContinuationAdvances_AndUnchangedPressureDoesNotRestartAnExhaustedPass()
    {
        var state = new SynchronizationEvidenceMaintenanceState();
        Assert.IsTrue(state.TryBeginBatch(out var start)); Assert.IsNull(start);
        Assert.IsFalse(state.TryBeginBatch(out _));
        var continuation = new EvidenceCompactionCursor("Revocation", "0001");
        state.CompleteBatch(continuation);
        Assert.IsTrue(state.TryBeginBatch(out var resumed)); Assert.AreEqual(continuation, resumed);
        state.CompleteBatch(null);
        for (var round = 0; round < 20; round++) state.CapacityPressure();
        Assert.IsFalse(state.TryBeginBatch(out _));
        state.ProofChanged(requestMaintenance: false);
        Assert.IsFalse(state.TryBeginBatch(out _), "Ordinary proof changes alone must not scan the history.");
        state.CapacityPressure();
        Assert.IsTrue(state.TryBeginBatch(out _));
        state.FailBatch();
        state.CapacityPressure();
        Assert.IsFalse(state.TryBeginBatch(out _), "A failed unchanged generation waits for a meaningful new opportunity.");
    }

    [TestMethod]
    public void ProofChangesDuringAPass_PreserveExplicitRecoveryRequestsWithoutFollowingOrdinaryTrafficForever()
    {
        var state = new SynchronizationEvidenceMaintenanceState();
        Assert.IsTrue(state.TryBeginBatch(out _));
        state.ProofChanged(requestMaintenance: false);
        state.CompleteBatch(null);
        Assert.IsFalse(state.TryBeginBatch(out _));
        state.ProofChanged();
        Assert.IsTrue(state.TryBeginBatch(out _));
        state.ProofChanged();
        state.CompleteBatch(null);
        Assert.IsTrue(state.TryBeginBatch(out var restart)); Assert.IsNull(restart);
        state.CompleteBatch(null);
        Assert.IsFalse(state.TryBeginBatch(out _));
    }

    [TestMethod]
    public async Task SnapshotGenerationPublishesOnlyAfterCommit_AndTimestampOnlyUpdatesDoNotDirtyProofs()
    {
        await using var vault = await SqliteTestVault.CreateAsync();
        await using var connection = vault.Connections.CreateConnection();
        await connection.OpenAsync();
        var user = Guid.NewGuid();
        // Use the same provider Guid binding as the snapshot's foreign-key value.
        await InsertRowAsync(connection, "Users", user, new Dictionary<string, object> { ["UId"] = user });
        var state = new SynchronizationEvidenceMaintenanceState();
        Assert.IsTrue(state.TryBeginBatch(out _)); state.CompleteBatch(null);
        using var session = new SqliteSession(vault.Connections, evidenceMaintenance: state);
        var repository = new UserSyncSnapshotRepository(session);
        var snapshot = new UserSyncSnapshot
        {
            UserId = user, OriginDeviceId = Guid.NewGuid(), OriginInstanceId = Guid.NewGuid(),
            OriginRevision = 1, UserKeyEpoch = 1, MembershipEpoch = 1, SnapshotHash = new byte[32],
            OriginSignPublicKey = new byte[32], OriginSignature = new byte[64], EnvelopePayload = [1],
            CreatedAtUtc = DateTimeOffset.UtcNow
        };
        await using (var transaction = await session.BeginTransactionAsync())
        {
            await repository.AddAsync(snapshot); await session.SaveChangesAsync();
            state.CapacityPressure(); Assert.IsFalse(state.TryBeginBatch(out _));
            await transaction.RollbackAsync();
        }
        state.CapacityPressure(); Assert.IsFalse(state.TryBeginBatch(out _));
        await repository.AddAsync(snapshot); await session.SaveChangesAsync();
        Assert.IsFalse(state.TryBeginBatch(out _));
        state.CapacityPressure(); Assert.IsTrue(state.TryBeginBatch(out _)); state.CompleteBatch(null);
        var retained = (await repository.GetAsync(user, snapshot.OriginDeviceId, snapshot.OriginInstanceId, 1))!;
        retained.ReceivedAtUtc = retained.ReceivedAtUtc.AddMinutes(1);
        repository.Update(retained); await session.SaveChangesAsync();
        state.CapacityPressure(); Assert.IsFalse(state.TryBeginBatch(out _));
        retained.Status = UserSyncSnapshotStatus.MergedReceipt;
        repository.Update(retained); await session.SaveChangesAsync();
        state.CapacityPressure(); Assert.IsTrue(state.TryBeginBatch(out _)); state.CompleteBatch(null);
    }
}
