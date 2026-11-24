using Microsoft.VisualStudio.TestTools.UnitTesting;
using PasswordManagerLocal.Common.Backend.Models;
using PasswordManagerLocal.Common.Backend.Exceptions;
using PasswordManagerLocal.Common.Backend.Persistence;
using PasswordManagerLocal.Common.Backend.Repositories;
using PasswordManagerLocal.Common.Backend.Services;
using PasswordManagerLocal.Common.Backend.Sync;
using PasswordManagerLocal.Common.Backend.Sync.Recovery;
using PasswordManagerLocal.Common.Tests.TestInfrastructure;
using PasswordManagerLocal.Common.Tests.TestInfrastructure.Services.Fixtures;

namespace PasswordManagerLocal.Common.Tests.Backend.Services;

[TestClass]
[TestCategory("Recovery")]
[TestCategory("TransactionHardening")]
public sealed class CanonicalRecoveryTransactionTests
{
    [TestMethod]
    public async Task VerificationGate_LeavesNoWriterOrReadSnapshot_AndAnotherAccountCommits()
    {
        await using var database = await SqliteRepositoryTestDatabase.CreateAsync();
        using var harness = await CoordinatorHarness.CreateAsync(database, true, useRealMembership: true, useDurableQueue: true);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        harness.Preparation.BeforeVerificationAsync = async ct =>
        {
            Assert.IsFalse(database.UnitOfWork.HasActiveTransaction);
            Assert.IsFalse(database.UnitOfWork.HasActiveReadSnapshot);
            Assert.IsFalse(database.UnitOfWork.HasPendingChanges);
            Assert.HasCount(0, harness.VersionClock.Observed);
            entered.TrySetResult();
            await release.Task.WaitAsync(ct);
        };
        var attempt = RecoverAsync(harness);
        try
        {
            await Task.WhenAny(entered.Task, attempt).WaitAsync(TimeSpan.FromSeconds(10));
            if (!entered.Task.IsCompleted)
            {
                var early = await attempt;
                Assert.Fail($"Recovery exited before the verification gate: {early.State}/{early.DiagnosticCode}.");
            }
            await entered.Task;
            // A separate session performs a real account write before releasing verification.
            using var other = database.OpenSession();
            var users = new UserRepository(other);
            var unrelated = new User { KeyEpoch = 1, MembershipEpoch = 1 };
            unrelated.GenerateIntegrityHash();
            await using (var write = await other.BeginTransactionAsync())
            {
                await users.AddAsync(unrelated);
                await other.SaveChangesAsync();
                await write.CommitAsync();
            }
            using var read = database.OpenSession();
            Assert.IsTrue(await new UserRepository(read).ExistsAsync(unrelated.UId));
            Assert.IsFalse(attempt.IsCompleted, "The other writer committed while cryptographic preparation was gated.");
            release.TrySetResult();
            Assert.AreEqual(UserDataRecoveryState.Recovered, (await attempt.WaitAsync(TimeSpan.FromSeconds(10))).State);
            Assert.IsTrue(harness.VersionClock.Observed.Count > 0);
        }
        finally { release.TrySetResult(); await attempt; }
    }

    [TestMethod]
    public async Task NewAuthenticatedCanonicalGeneration_RejectsPreparedOutputAndPreservesNewLocalPassword()
    {
        await using var database = await SqliteRepositoryTestDatabase.CreateAsync();
        using var harness = await CoordinatorHarness.CreateAsync(database, true, useRealMembership: true, useDurableQueue: true);
        byte[]? expected = null;
        harness.Preparation.AfterPreparationAsync = async ct =>
        {
            using var writer = database.OpenSession();
            var users = new UserRepository(writer);
            var current = (await users.GetByIdWithRelationsAsync(harness.UserId, ct))!;
            using var newer = await harness.CreateNewerCanonicalAsync();
            newer.ApplyTo(current);
            users.Update(current);
            var checkpoints = new UserCanonicalCheckpointRepository(writer);
            var health = new UserCanonicalHealthService(checkpoints, harness.Verification,
                new UserSyncFaultService(new UserSyncFaultRepository(writer)), harness.LocalIdentity);
            await using var transaction = await writer.BeginTransactionAsync(ct);
            await health.UpdateCheckpointAsync(current, ct);
            await writer.SaveChangesAsync(ct);
            await transaction.CommitAsync(ct);
            expected = current.EncryptedPayload.ToArray();
        };
        Assert.AreEqual(UserDataRecoveryState.TransactionConflict, (await RecoverAsync(harness)).State);
        using var read = database.OpenSession();
        var persisted = (await new UserRepository(read).GetByIdAsNoTrackingAsync(harness.UserId))!;
        CollectionAssert.AreEqual(expected!, persisted.EncryptedPayload);
        using var verification = await harness.Verification.VerifyCanonicalAsync(persisted, harness.Key,
            UserSyncKeyConfidence.ExplicitlyTrusted);
        Assert.IsTrue(verification.IsHealthy);
        Assert.IsTrue(verification.VerifiedBundle!.UserPasswordsData.Passwords.Any(item => item.Name == "newer-local-password"));
        var checkpoint = (await new UserCanonicalCheckpointRepository(read).GetAsync(harness.UserId))!;
        UserCanonicalCheckpointUtil.Verify(persisted, checkpoint, harness.LocalIdentity);
        Assert.AreEqual(2L, checkpoint.CheckpointSequence);
        await AssertNoPublicationAsync(database, harness);
        AssertPreparedCleared(harness);
        Assert.HasCount(0, harness.VersionClock.Observed);
    }

    [TestMethod]
    public async Task DeletionBarrierCommittedAfterPreparation_WinsAndNeverRecreatesAccount()
    {
        await using var database = await SqliteRepositoryTestDatabase.CreateAsync();
        using var harness = await CoordinatorHarness.CreateAsync(database, true, useRealMembership: true, useDurableQueue: true);
        harness.Preparation.AfterPreparationAsync = async ct =>
        {
            using var writer = database.OpenSession();
            await using var transaction = await writer.BeginTransactionAsync(ct);
            await new DeletedUserBarrierRepository(writer).AddAsync(new DeletedUserBarrier
            {
                UserId = harness.UserId, DeletionOperationId = Guid.NewGuid(), DeletionGeneration = Guid.NewGuid(),
                OriginDeviceId = harness.LocalIdentity.LocalDeviceId, OriginInstanceId = harness.LocalIdentity.OriginInstanceId,
                OriginSequence = 1, KeyEpoch = 1, MembershipEpoch = 1,
                OperationHash = new byte[32], OriginSignPublicKey = harness.LocalIdentity.SignPublicKey,
                OriginSignature = new byte[64], DeletedAtUtc = DateTimeOffset.UtcNow,
                AppliedAtUtc = DateTimeOffset.UtcNow, LastUpdatedAtUtc = DateTimeOffset.UtcNow
            }, ct);
            var users = new UserRepository(writer);
            users.Delete((await users.GetByIdAsync(harness.UserId, ct))!);
            await writer.SaveChangesAsync(ct);
            await transaction.CommitAsync(ct);
        };
        Assert.AreEqual(UserDataRecoveryState.AccountDeleted, (await RecoverAsync(harness)).State);
        using var read = database.OpenSession();
        Assert.IsTrue(await new DeletedUserBarrierRepository(read).ExistsAsync(harness.UserId));
        Assert.IsFalse(await new UserRepository(read).ExistsAsync(harness.UserId));
        Assert.HasCount(0, await new UserRevisionKnowledgeRepository(read).ListForUserAsync(harness.UserId));
        await AssertNoPublicationAsync(database, harness);
        AssertPreparedCleared(harness);
    }

    [TestMethod]
    [DataRow("key-epoch")]
    [DataRow("membership-epoch")]
    [DataRow("local-authority")]
    [DataRow("historical-authority")]
    [DataRow("removal-cutoff")]
    [DataRow("control-conflict")]
    [DataRow("isolated-candidate")]
    [DataRow("removed-candidate")]
    [DataRow("candidate-hash")]
    [DataRow("candidate-bytes")]
    [DataRow("terminal-fork")]
    [DataRow("new-candidate")]
    [DataRow("checkpoint")]
    [DataRow("recovery-backoff")]
    [DataRow("journal-cleanup")]
    [DataRow("merged-knowledge")]
    [DataRow("local-revision")]
    [DataRow("local-identity")]
    [DataRow("critical-ingress")]
    public async Task ChangedAuthoritativeDependency_AbortsAllSpeculativeTransitions(string change)
    {
        await using var database = await SqliteRepositoryTestDatabase.CreateAsync();
        using var harness = await CoordinatorHarness.CreateAsync(database, true, useRealMembership: true, useDurableQueue: true);
        var damaged = (await database.Users.GetByIdAsync(harness.UserId))!.EncryptedPayload.ToArray();
        await database.UserSyncFaults.AddAsync(new UserSyncFault
        {
            UserId = harness.UserId, Scope = UserSyncFaultScope.LocalCanonical,
            Kind = UserSyncFaultKind.CanonicalRootDecryptFailure, Status = UserSyncHealthStatus.AwaitingEvidence,
            RecoveryAttemptCount = 2, BlocksLogin = true, BlocksPublishing = true
        });
        await database.UnitOfWork.SaveChangesAsync();
        harness.Preparation.AfterPreparationAsync = async ct =>
        {
            Assert.IsFalse(database.UnitOfWork.HasActiveTransaction);
            Assert.IsFalse(database.UnitOfWork.HasActiveReadSnapshot);
            Assert.IsFalse(database.UnitOfWork.HasPendingChanges);
            using var writer = database.OpenSession();
            var sql = change switch
            {
                "key-epoch" => "UPDATE Users SET KeyEpoch = 2 WHERE UId = $user;",
                "membership-epoch" => "UPDATE Users SET MembershipEpoch = 2 WHERE UId = $user;",
                "local-authority" => "UPDATE UserMembershipAuthorizations SET IsActive = 0 WHERE UserId = $user AND DeviceId = $local;",
                "historical-authority" => "UPDATE UserMembershipAuthorizations SET MaximumKeyEpoch = 0 WHERE UserId = $user AND DeviceId <> $local;",
                "removal-cutoff" => "INSERT INTO UserOriginRemovalCutoffs SELECT $id, AuthorizationId, CreatedAtUtc, DeviceId, 0, 0, OriginInstanceId, zeroblob(32), $id, 2, UserId, 1 FROM UserMembershipAuthorizations WHERE UserId = $user AND DeviceId <> $local;",
                "control-conflict" => "INSERT INTO UserControlStates (UserId, AppliedKeyEpoch, AppliedMembershipEpoch, HasConflict, LastUpdatedAtUtc, LocalOriginInstanceId, NextOriginSequence) VALUES ($user, 1, 1, 1, '2026-01-01', $id, 1);",
                "isolated-candidate" => "UPDATE UserSyncSnapshots SET Status = 5 WHERE UserId = $user;",
                "removed-candidate" => "DELETE FROM UserSyncSnapshots WHERE UserId = $user;",
                "candidate-hash" => "UPDATE UserSyncSnapshots SET SnapshotHash = zeroblob(32) WHERE UserId = $user;",
                "candidate-bytes" => "UPDATE UserSyncSnapshots SET EnvelopePayload = zeroblob(length(EnvelopePayload)) WHERE UserId = $user;",
                "terminal-fork" => "UPDATE UserSyncSnapshots SET Status = 2 WHERE UserId = $user;",
                "new-candidate" => "INSERT INTO UserSyncSnapshots SELECT $id, ConflictingEnvelopePayload, ConflictingSnapshotHash, CreatedAtUtc, EnvelopePayload, LastReceivedFromDeviceId, MembershipEpoch, $id, $id, OriginRevision, OriginSignPublicKey, OriginSignature, QuarantineReason, ReceivedAtUtc, SnapshotHash, Status, UserId, UserKeyEpoch FROM UserSyncSnapshots WHERE UserId = $user;",
                "checkpoint" => "UPDATE UserCanonicalCheckpoints SET CheckpointSequence = CheckpointSequence + 1 WHERE UserId = $user;",
                "recovery-backoff" => "UPDATE UserSyncFaults SET RecoveryAttemptCount = 3 WHERE UserId = $user;",
                "journal-cleanup" => "INSERT INTO UserRecoveryJournals (Id, UserId, State, CreatedAtUtc, ProtectedKey, EncryptedPayload, PayloadHash) VALUES ($id, $user, 0, '2026-01-01', zeroblob(32), zeroblob(32), zeroblob(32));",
                "merged-knowledge" => "INSERT INTO UserRevisionKnowledge VALUES ($user, $id, $id, 1, 2, 2, zeroblob(32), '2026-01-01');",
                "local-revision" => "INSERT INTO UserSyncStates VALUES ($user, zeroblob(32), '2026-01-01', $id, 17);",
                "local-identity" => "SELECT 1;",
                "critical-ingress" => "INSERT INTO CriticalControlIngress VALUES (zeroblob(32), $user, $id, zeroblob(32), $local, x'01');",
                _ => throw new AssertFailedException("Unknown dependency mutation.")
            };
            await writer.ExecuteFixtureSqlAsync(sql, ("$user", harness.UserId),
                ("$local", harness.LocalIdentity.LocalDeviceId), ("$id", Guid.NewGuid()));
            if (change == "local-identity") harness.LocalIdentity.OriginInstanceId = Guid.NewGuid();
        };
        Assert.AreEqual(UserDataRecoveryState.TransactionConflict, (await RecoverAsync(harness)).State, change);
        using var read = database.OpenSession();
        var user = (await new UserRepository(read).GetByIdAsNoTrackingAsync(harness.UserId))!;
        CollectionAssert.AreEqual(damaged, user.EncryptedPayload);
        var fault = (await new UserSyncFaultRepository(read).ListForUserAsync(harness.UserId)).Single();
        Assert.AreEqual(UserSyncHealthStatus.AwaitingEvidence, fault.Status);
        Assert.AreEqual(change == "recovery-backoff" ? 3 : 2, fault.RecoveryAttemptCount);
        Assert.IsNull(fault.RecoveredAtUtc);
        Assert.IsNull(fault.SupersedingRevision);
        Assert.HasCount(0, harness.VersionClock.Observed);
        Assert.IsFalse(database.UnitOfWork.HasPendingChanges);
        Assert.IsFalse(database.UnitOfWork.HasActiveTransaction);
        await AssertNoPublicationAsync(database, harness);
        AssertPreparedCleared(harness);
    }

    [TestMethod]
    [DataRow("capture")]
    [DataRow("verification")]
    [DataRow("reconstruction")]
    [DataRow("prepared")]
    [DataRow("revalidation")]
    [DataRow("commit")]
    public async Task CancellationAtBoundary_RollsBackAndFreshAttemptCanRecover(string boundary)
    {
        await using var database = await SqliteRepositoryTestDatabase.CreateAsync();
        using var harness = await CoordinatorHarness.CreateAsync(database, true, useRealMembership: true, useDurableQueue: true);
        using var cancel = new CancellationTokenSource();
        Task Cancel(CancellationToken ct) { cancel.Cancel(); ct.ThrowIfCancellationRequested(); return Task.CompletedTask; }
        switch (boundary)
        {
            case "capture": harness.RecoveryStore.AfterCaptureAsync = Cancel; break;
            case "verification": harness.Preparation.BeforeVerificationAsync = Cancel; break;
            case "reconstruction": harness.Preparation.AfterVerificationAsync = Cancel; break;
            case "prepared": harness.Preparation.AfterPreparationAsync = Cancel; break;
            case "revalidation": harness.RecoveryStore.BeforeRevalidationAsync = Cancel; break;
            case "commit": harness.RecoveryWork.BeforeCommitAsync = Cancel; break;
        }
        var before = (await database.Users.GetByIdAsync(harness.UserId))!.EncryptedPayload.ToArray();
        await Assert.ThrowsAsync<OperationCanceledException>(() => RecoverAsync(harness, cancel.Token));
        await AssertUncommittedAsync(database, harness, before);
        if (harness.Preparation.LastPrepared is not null) AssertPreparedCleared(harness);
        harness.RecoveryStore.AfterCaptureAsync = null;
        harness.Preparation.BeforeVerificationAsync = null;
        harness.Preparation.AfterVerificationAsync = null;
        harness.Preparation.AfterPreparationAsync = null;
        harness.RecoveryStore.BeforeRevalidationAsync = null;
        harness.RecoveryWork.BeforeCommitAsync = null;
        Assert.AreEqual(UserDataRecoveryState.Recovered, (await RecoverAsync(harness)).State);
    }

    [TestMethod]
    [DataRow("Users", "UPDATE")]
    [DataRow("UserCanonicalCheckpoints", "UPDATE")]
    [DataRow("UserRevisionKnowledge", "INSERT")]
    [DataRow("UserSyncSnapshots", "INSERT")]
    [DataRow("UserLoginIdentityStates", "INSERT")]
    [DataRow("SyncQueueItems", "INSERT")]
    [DataRow("UserSyncFaults", "UPDATE")]
    public async Task SqliteWriteFailure_RollsBackEveryRecoveryArtifact_ThenRetries(string table, string operation)
    {
        await using var database = await SqliteRepositoryTestDatabase.CreateAsync();
        using var harness = await CoordinatorHarness.CreateAsync(database, true, useRealMembership: true, useDurableQueue: true);
        var before = (await database.Users.GetByIdAsync(harness.UserId))!.EncryptedPayload.ToArray();
        // Identifiers are fixed DataRow constants, never user input. UPDATE faults fails after
        // publication, exercising rollback of changes already flushed through nested savepoints.
        await database.UnitOfWork.ExecuteFixtureSqlAsync(
            $"CREATE TRIGGER recovery_failure BEFORE {operation} ON \"{table}\" BEGIN SELECT RAISE(ABORT, 'injected recovery failure'); END;");
        Assert.AreEqual(UserDataRecoveryState.Failed, (await RecoverAsync(harness)).State);
        await AssertUncommittedAsync(database, harness, before);
        AssertPreparedCleared(harness);
        await database.UnitOfWork.ExecuteFixtureSqlAsync("DROP TRIGGER recovery_failure;");
        Assert.AreEqual(UserDataRecoveryState.Recovered, (await RecoverAsync(harness)).State);
    }

    [TestMethod]
    public async Task FinalCommitFailure_RollsBackFlushedState_AndRetryPublishesOnlyOneRevision()
    {
        await using var database = await SqliteRepositoryTestDatabase.CreateAsync();
        using var harness = await CoordinatorHarness.CreateAsync(database, true, useRealMembership: true, useDurableQueue: true);
        var before = (await database.Users.GetByIdAsync(harness.UserId))!.EncryptedPayload.ToArray();
        harness.RecoveryWork.BeforeCommitAsync = _ => throw new IOException("Injected final commit failure.");
        Assert.AreEqual(UserDataRecoveryState.Failed, (await RecoverAsync(harness)).State);
        await AssertUncommittedAsync(database, harness, before);
        harness.RecoveryWork.BeforeCommitAsync = null;
        var recovered = await RecoverAsync(harness);
        Assert.AreEqual(UserDataRecoveryState.Recovered, recovered.State);
        Assert.AreEqual(1L, recovered.RecoveryRevision);
    }

    [TestMethod]
    [DataRow("Snapshots")]
    [DataRow("Recovery")]
    [DataRow("Content")]
    public async Task CapacityRefusal_RetainsCanonicalAndEvidence_WithoutLeakingPreparedState(string category)
    {
        await using var database = await SqliteRepositoryTestDatabase.CreateAsync();
        using var harness = await CoordinatorHarness.CreateAsync(database, true, useRealMembership: true, useDurableQueue: true);
        var before = (await database.Users.GetByIdAsync(harness.UserId))!.EncryptedPayload.ToArray();
        await database.UnitOfWork.ExecuteFixtureSqlAsync(
            "UPDATE StorageBudgets SET LimitBytes = MAX(UsedBytes, 1) WHERE Category = $category;", ("$category", category));
        Assert.AreEqual(UserDataRecoveryState.Failed, (await RecoverAsync(harness)).State);
        await AssertUncommittedAsync(database, harness, before);
        AssertPreparedCleared(harness);
    }

    [TestMethod]
    public async Task MutatedPreparedCiphertext_CannotBypassPublicationHealthGate()
    {
        await using var database = await SqliteRepositoryTestDatabase.CreateAsync();
        using var harness = await CoordinatorHarness.CreateAsync(database, true, useRealMembership: true, useDurableQueue: true);
        var before = (await database.Users.GetByIdAsync(harness.UserId))!.EncryptedPayload.ToArray();
        harness.Preparation.AfterPreparationAsync = ct =>
        {
            var prepared = harness.Preparation.LastPrepared!;
            prepared.User.EncryptedPayload[0] ^= 0x55;
            prepared.User.GenerateIntegrityHash();
            Assert.IsFalse(prepared.MatchesVerifiedOutput(prepared.User));
            return Task.CompletedTask;
        };
        Assert.AreEqual(UserDataRecoveryState.Failed, (await RecoverAsync(harness)).State);
        await AssertUncommittedAsync(database, harness, before);
    }

    [TestMethod]
    public async Task DiscardedPreparation_FreshReadAndAttemptSeeOnlyOriginalDurableEvidence()
    {
        await using var database = await SqliteRepositoryTestDatabase.CreateAsync();
        using var harness = await CoordinatorHarness.CreateAsync(database, true, useRealMembership: true, useDurableQueue: true);
        var before = (await database.Users.GetByIdAsync(harness.UserId))!.EncryptedPayload.ToArray();
        harness.Preparation.AfterPreparationAsync = _ => throw new IOException("Simulated lost preparation.");
        Assert.AreEqual(UserDataRecoveryState.Failed, (await RecoverAsync(harness)).State);
        await AssertUncommittedAsync(database, harness, before);
        harness.Preparation.AfterPreparationAsync = null;
        Assert.AreEqual(UserDataRecoveryState.Recovered, (await RecoverAsync(harness)).State);
        using var fresh = database.OpenSession();
        var user = (await new UserRepository(fresh).GetByIdAsNoTrackingAsync(harness.UserId))!;
        var checkpoint = (await new UserCanonicalCheckpointRepository(fresh).GetAsync(harness.UserId))!;
        UserCanonicalCheckpointUtil.Verify(user, checkpoint, harness.LocalIdentity);
        Assert.AreEqual(UserLoginIdentityStatus.Active,
            (await new UserRepository(fresh).GetLoginIdentityStateAsync(harness.UserId))!.Status);
        Assert.HasCount(1, await database.ReadQueueRowsAsync());
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task SpeculativeCryptographicConflict_BecomesTerminalOnlyWhileItsInputsRemainCurrent(bool stale)
    {
        await using var database = await SqliteRepositoryTestDatabase.CreateAsync();
        using var harness = await CoordinatorHarness.CreateAsync(database, true, useRealMembership: true, useDurableQueue: true);
        var before = (await database.Users.GetByIdAsync(harness.UserId))!.EncryptedPayload.ToArray();
        harness.Preparation.AfterVerificationAsync = async ct =>
        {
            if (stale)
            {
                using var writer = database.OpenSession();
                await writer.ExecuteFixtureSqlAsync("UPDATE UserSyncSnapshots SET Status = 5 WHERE UserId = $user;",
                    ("$user", harness.UserId));
            }
            throw new RecoveryEvidenceConflictException("controlled-key-material-conflict");
        };
        Assert.AreEqual(stale ? UserDataRecoveryState.TransactionConflict : UserDataRecoveryState.ControlPlaneConflict,
            (await RecoverAsync(harness)).State);
        using var read = database.OpenSession();
        CollectionAssert.AreEqual(before, (await new UserRepository(read).GetByIdAsNoTrackingAsync(harness.UserId))!.EncryptedPayload);
        var faults = await new UserSyncFaultRepository(read).ListForUserAsync(harness.UserId);
        if (stale) Assert.HasCount(0, faults);
        else
        {
            Assert.HasCount(1, faults);
            Assert.AreEqual(UserSyncHealthStatus.TerminalConflict, faults.Single().Status);
            Assert.AreEqual("controlled-key-material-conflict", faults.Single().DiagnosticCode);
        }
        await AssertNoPublicationAsync(database, harness);
        Assert.HasCount(0, harness.VersionClock.Observed);
        Assert.IsFalse(database.UnitOfWork.HasPendingChanges);
    }

    private static Task<UserDataRecoveryResult> RecoverAsync(CoordinatorHarness harness, CancellationToken ct = default) =>
        harness.Coordinator.TryRecoverAsync(harness.UserId, harness.Key, UserSyncKeyConfidence.AuthenticatedSession,
            UserDataRecoveryTrigger.ManualRetry, ct);

    private static async Task AssertNoPublicationAsync(SqliteRepositoryTestDatabase database, CoordinatorHarness harness)
    {
        using var read = database.OpenSession();
        var snapshots = await new UserSyncSnapshotRepository(read).ListForUserAsync(harness.UserId);
        Assert.IsFalse(snapshots.Any(row => row.Status == UserSyncSnapshotStatus.LocalPublished));
        Assert.AreEqual(0, harness.Activation.ActivatePendingSyncsCalls);
        Assert.HasCount(0, await database.ReadQueueRowsAsync());
        Assert.IsNull(await new UserRepository(read).GetLoginIdentityStateAsync(harness.UserId));
    }

    private static async Task AssertUncommittedAsync(SqliteRepositoryTestDatabase database, CoordinatorHarness harness, byte[] before)
    {
        using var read = database.OpenSession();
        var user = (await new UserRepository(read).GetByIdAsNoTrackingAsync(harness.UserId))!;
        CollectionAssert.AreEqual(before, user.EncryptedPayload);
        Assert.AreEqual(1L, (await new UserCanonicalCheckpointRepository(read).GetAsync(harness.UserId))!.CheckpointSequence);
        var snapshots = await new UserSyncSnapshotRepository(read).ListForUserAsync(harness.UserId);
        Assert.HasCount(1, snapshots);
        Assert.AreEqual(UserSyncSnapshotStatus.RecoveryCandidate, snapshots.Single().Status);
        Assert.HasCount(0, await new UserRevisionKnowledgeRepository(read).ListForUserAsync(harness.UserId));
        var faults = await new UserSyncFaultRepository(read).ListForUserAsync(harness.UserId);
        Assert.IsFalse(faults.Any(fault => fault.Status == UserSyncHealthStatus.Recovered));
        Assert.HasCount(0, harness.VersionClock.Observed);
        Assert.IsFalse(database.UnitOfWork.HasPendingChanges);
        Assert.IsFalse(database.UnitOfWork.HasActiveTransaction);
        Assert.IsFalse(database.UnitOfWork.HasActiveReadSnapshot);
        await AssertNoPublicationAsync(database, harness);
    }

    private static void AssertPreparedCleared(CoordinatorHarness harness)
    {
        var prepared = harness.Preparation.LastPrepared;
        Assert.IsNotNull(prepared);
        Assert.IsTrue(prepared.User.EncryptedPayload.All(value => value == 0));
        Assert.IsTrue(prepared.User.EncryptedGeneralUserDataPayload.All(value => value == 0));
        Assert.IsTrue(prepared.User.EncryptedUserPasswordsDataPayload.All(value => value == 0));
        Assert.IsTrue(prepared.User.EncryptedUserDevicesDataPayload.All(value => value == 0));
    }
}
