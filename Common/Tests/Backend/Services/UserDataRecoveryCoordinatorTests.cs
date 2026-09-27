using Microsoft.VisualStudio.TestTools.UnitTesting;
using NSec.Cryptography;
using NSecKey = NSec.Cryptography.Key;
using PasswordManagerLocal.Common.Backend.Abstractions.Services;
using PasswordManagerLocal.Common.Backend.Models;
using PasswordManagerLocal.Common.Backend.Models.Encrypted;
using PasswordManagerLocal.Common.Backend.Security;
using PasswordManagerLocal.Common.Backend.Services;
using PasswordManagerLocal.Common.Backend.Sync;
using PasswordManagerLocal.Common.Backend.Utils;
using PasswordManagerLocal.Common.Tests.Fakes;
using PasswordManagerLocal.Common.Tests.TestInfrastructure;
using System.Text.Json;

using MSTestAssert = Microsoft.VisualStudio.TestTools.UnitTesting.Assert;

using PasswordManagerLocal.Common.Tests.TestInfrastructure.Services.Fixtures;
using PasswordManagerLocal.Common.Tests.TestInfrastructure.Services.TestDoubles;
namespace PasswordManagerLocal.Common.Tests.Backend.Services;

[TestClass]
public sealed class UserDataRecoveryCoordinatorTests
{
    [TestMethod]
    [TestCategory("Backend")]
    [TestCategory("Integration")]
    [TestCategory("Recovery")]
    public async Task TryRecoverAsync_CorruptCanonicalAndHealthyCandidate_CommitsCompleteRecoveryAtomically()
    {
        await using var database = await SqliteIntegrationTestDatabase.CreateAsync();
        using var harness = await CoordinatorHarness.CreateAsync(database, includeHealthyCandidate: true);

        var result = await harness.Coordinator.TryRecoverAsync(
            harness.UserId,
            harness.Key,
            UserSyncKeyConfidence.UnconfirmedPassword,
            UserDataRecoveryTrigger.Login);

        MSTestAssert.AreEqual(UserDataRecoveryState.Recovered, result.State);
        MSTestAssert.AreEqual(1, result.HealthyCandidateCount);
        MSTestAssert.IsTrue(result.RecoveryRevision > 0);
        database.Db.ChangeTracker.Clear();
        var recovered = await database.Users.GetByIdAsync(harness.UserId);
        MSTestAssert.IsNotNull(recovered);
        using (var verification = await harness.Verification.VerifyCanonicalAsync(
                   recovered,
                   harness.Key,
                   UserSyncKeyConfidence.ExplicitlyTrusted))
        {
            MSTestAssert.IsTrue(verification.IsHealthy);
            MSTestAssert.AreEqual("remote-recovered-user", verification.VerifiedBundle!.GeneralUserData.Username);
            MSTestAssert.IsTrue(verification.VerifiedBundle.UserPasswordsData.Passwords.Any(item => item.Name == "remote-password"));
        }

        var checkpoint = await database.UserCanonicalCheckpoints.GetAsync(harness.UserId);
        MSTestAssert.IsNotNull(checkpoint);
        UserCanonicalCheckpointUtil.Verify(recovered, checkpoint, harness.LocalIdentity);
        MSTestAssert.AreEqual(2L, checkpoint.CheckpointSequence);

        var snapshots = await database.UserSyncSnapshots.ListForUserAsync(harness.UserId);
        MSTestAssert.AreEqual(2, snapshots.Count);
        MSTestAssert.IsTrue(snapshots.Any(snapshot => snapshot.Status == UserSyncSnapshotStatus.LocalPublished));
        MSTestAssert.IsTrue(snapshots.Any(snapshot => snapshot.Status == UserSyncSnapshotStatus.MergedReceipt));
        MSTestAssert.HasCount(1, harness.Queue.EnqueuedItems);
        MSTestAssert.AreEqual(1, harness.Activation.ActivatePendingSyncsCalls);

        var faults = await database.UserSyncFaults.ListForUserAsync(harness.UserId);
        MSTestAssert.IsTrue(faults.Any(fault =>
            fault.Scope == UserSyncFaultScope.LocalCanonical &&
            fault.Status == UserSyncHealthStatus.Recovered &&
            fault.SupersedingRevision == result.RecoveryRevision));
        var identity = await database.Users.GetLoginIdentityStateAsync(harness.UserId);
        MSTestAssert.IsNotNull(identity);
        MSTestAssert.AreEqual(UserLoginIdentityStatus.Active, identity.Status);
        CollectionAssert.AreEqual(recovered.UsernameHash, identity.UsernameHash);
    }

    [TestMethod]
    [TestCategory("Backend")]
    [TestCategory("Integration")]
    [TestCategory("Recovery")]
    public async Task TryRecoverAsync_NoHealthySource_RemainsBlockedWithoutFabricatingData()
    {
        await using var database = await SqliteIntegrationTestDatabase.CreateAsync();
        using var harness = await CoordinatorHarness.CreateAsync(database, includeHealthyCandidate: false);
        var damagedCiphertext = (await database.Users.GetByIdAsync(harness.UserId))!.EncryptedPayload.ToArray();

        var result = await harness.Coordinator.TryRecoverAsync(
            harness.UserId,
            harness.Key,
            UserSyncKeyConfidence.AuthenticatedSession,
            UserDataRecoveryTrigger.ManualRetry);

        MSTestAssert.AreEqual(UserDataRecoveryState.AwaitingPeerEvidence, result.State);
        database.Db.ChangeTracker.Clear();
        var user = await database.Users.GetByIdAsync(harness.UserId);
        MSTestAssert.IsNotNull(user);
        CollectionAssert.AreEqual(damagedCiphertext, user.EncryptedPayload);
        MSTestAssert.HasCount(0, harness.Queue.EnqueuedItems);
        MSTestAssert.IsNull(await database.UserSyncSnapshots.GetLatestLocalAsync(
            harness.UserId,
            harness.LocalIdentity.LocalDeviceId,
            harness.LocalIdentity.OriginInstanceId,
            1));
        var faults = await database.UserSyncFaults.ListForUserAsync(harness.UserId);
        MSTestAssert.IsTrue(faults.Any(fault =>
            fault.Scope == UserSyncFaultScope.LocalCanonical &&
            fault.Status == UserSyncHealthStatus.AwaitingEvidence &&
            fault.BlocksPublishing));
    }

    [TestMethod]
    [TestCategory("Backend")]
    [TestCategory("Integration")]
    [TestCategory("Security")]
    [TestCategory("Recovery")]
    public async Task TryRecoverAsync_UnconfirmedWrongPassword_DoesNotAttributeCanonicalCorruption()
    {
        await using var database = await SqliteIntegrationTestDatabase.CreateAsync();
        using var harness = await CoordinatorHarness.CreateAsync(database, includeHealthyCandidate: true);
        using var wrongKey = EncryptionKey.Create();

        var result = await harness.Coordinator.TryRecoverAsync(
            harness.UserId,
            wrongKey,
            UserSyncKeyConfidence.UnconfirmedPassword,
            UserDataRecoveryTrigger.Login);

        MSTestAssert.AreEqual(UserDataRecoveryState.KeyNotTrusted, result.State);
        database.Db.ChangeTracker.Clear();
        var faults = await database.UserSyncFaults.ListForUserAsync(harness.UserId);
        MSTestAssert.IsFalse(faults.Any(fault => fault.Scope == UserSyncFaultScope.LocalCanonical));
        var evidence = (await database.UserSyncSnapshots.ListForUserAsync(harness.UserId)).Single();
        MSTestAssert.AreEqual(UserSyncSnapshotStatus.RecoveryCandidate, evidence.Status);
        MSTestAssert.HasCount(0, harness.Queue.EnqueuedItems);
    }

    [TestMethod]
    [TestCategory("Backend")]
    [TestCategory("Integration")]
    [TestCategory("Recovery")]
    public async Task TryRecoverAsync_DatabaseStructuralFailure_BlocksRowLevelRecovery()
    {
        await using var database = await SqliteIntegrationTestDatabase.CreateAsync();
        using var harness = await CoordinatorHarness.CreateAsync(
            database,
            includeHealthyCandidate: true,
            databaseHealth: new FixedDatabaseHealthService(false, "sqlite-quick-check-failed"));

        var result = await harness.Coordinator.TryRecoverAsync(
            harness.UserId,
            harness.Key,
            UserSyncKeyConfidence.AuthenticatedSession,
            UserDataRecoveryTrigger.ManualRetry);

        MSTestAssert.AreEqual(UserDataRecoveryState.DatabaseUnhealthy, result.State);
        MSTestAssert.HasCount(0, harness.Queue.EnqueuedItems);
    }

    [TestMethod]
    [TestCategory("Backend")]
    [TestCategory("Integration")]
    [TestCategory("Recovery")]
    public async Task TryRecoverAsync_DeletionBarrierAlwaysWins()
    {
        await using var database = await SqliteIntegrationTestDatabase.CreateAsync();
        using var harness = await CoordinatorHarness.CreateAsync(database, includeHealthyCandidate: true);
        await database.DeletedUserBarriers.AddAsync(new DeletedUserBarrier
        {
            UserId = harness.UserId,
            DeletionOperationId = Guid.NewGuid(),
            DeletionGeneration = Guid.NewGuid(),
            OriginDeviceId = Guid.NewGuid(),
            OriginInstanceId = Guid.NewGuid(),
            OriginSequence = 1,
            KeyEpoch = 1,
            MembershipEpoch = 1,
            DeletedAtUtc = DateTimeOffset.UtcNow,
            AppliedAtUtc = DateTimeOffset.UtcNow,
            LastUpdatedAtUtc = DateTimeOffset.UtcNow
        });
        await database.UnitOfWork.SaveChangesAsync();

        var result = await harness.Coordinator.TryRecoverAsync(
            harness.UserId,
            harness.Key,
            UserSyncKeyConfidence.AuthenticatedSession,
            UserDataRecoveryTrigger.ManualRetry);

        MSTestAssert.AreEqual(UserDataRecoveryState.AccountDeleted, result.State);
        MSTestAssert.HasCount(0, harness.Queue.EnqueuedItems);
        MSTestAssert.IsNull(await database.Users.GetLoginIdentityStateAsync(harness.UserId));
    }

    [TestMethod]
    [TestCategory("Backend")]
    [TestCategory("Integration")]
    [TestCategory("Recovery")]
    public async Task TryRecoverAsync_BackoffBlocksMaintenanceButNewEvidenceBypassesIt()
    {
        await using var database = await SqliteIntegrationTestDatabase.CreateAsync();
        using var harness = await CoordinatorHarness.CreateAsync(database, includeHealthyCandidate: true);
        await database.UserSyncFaults.AddAsync(new UserSyncFault
        {
            UserId = harness.UserId,
            Scope = UserSyncFaultScope.LocalCanonical,
            Kind = UserSyncFaultKind.CanonicalRootDecryptFailure,
            Status = UserSyncHealthStatus.AwaitingEvidence,
            AffectedComponent = UserDataBlobKind.All.ToString(),
            KeyEpoch = 1,
            MembershipEpoch = 1,
            RecoveryAttemptCount = 3,
            LastRecoveryAttemptAtUtc = DateTimeOffset.UtcNow,
            NextRecoveryAttemptAtUtc = DateTimeOffset.UtcNow.AddHours(1),
            BlocksPublishing = true,
            BlocksLogin = true,
            BlocksGarbageCollection = true
        });
        await database.UnitOfWork.SaveChangesAsync();

        var deferred = await harness.Coordinator.TryRecoverAsync(
            harness.UserId,
            harness.Key,
            UserSyncKeyConfidence.AuthenticatedSession,
            UserDataRecoveryTrigger.PeriodicMaintenance);
        MSTestAssert.AreEqual(UserDataRecoveryState.BackoffActive, deferred.State);

        var recovered = await harness.Coordinator.TryRecoverAsync(
            harness.UserId,
            harness.Key,
            UserSyncKeyConfidence.AuthenticatedSession,
            UserDataRecoveryTrigger.HealthyCandidateReceived);
        MSTestAssert.AreEqual(UserDataRecoveryState.Recovered, recovered.State);
    }

    [TestMethod]
    [TestCategory("Backend")]
    [TestCategory("Integration")]
    [TestCategory("Security")]
    [TestCategory("Recovery")]
    public async Task TryRecoverAsync_InvalidSignatureCandidate_IsolatedAndIgnored()
    {
        await using var database = await SqliteIntegrationTestDatabase.CreateAsync();
        using var harness = await CoordinatorHarness.CreateAsync(database, includeHealthyCandidate: true);
        var candidate = (await database.UserSyncSnapshots.ListForUserAsync(harness.UserId)).Single();
        var envelope = JsonSerializer.Deserialize(
            candidate.EnvelopePayload,
            BackendJsonSerializerContext.Default.UserSnapshotEnvelope);
        MSTestAssert.IsNotNull(envelope);
        envelope.OriginSignature[0] ^= 0x7F;
        candidate.OriginSignature = envelope.OriginSignature.ToArray();
        candidate.EnvelopePayload = JsonSerializer.SerializeToUtf8Bytes(
            envelope,
            BackendJsonSerializerContext.Default.UserSnapshotEnvelope);
        database.UserSyncSnapshots.Update(candidate);
        await database.UnitOfWork.SaveChangesAsync();
        database.Db.ChangeTracker.Clear();

        var result = await harness.Coordinator.TryRecoverAsync(
            harness.UserId,
            harness.Key,
            UserSyncKeyConfidence.AuthenticatedSession,
            UserDataRecoveryTrigger.ManualRetry);

        MSTestAssert.AreEqual(UserDataRecoveryState.AwaitingPeerEvidence, result.State);
        database.Db.ChangeTracker.Clear();
        var isolated = (await database.UserSyncSnapshots.ListForUserAsync(harness.UserId)).Single();
        MSTestAssert.AreEqual(UserSyncSnapshotStatus.IsolatedCorrupt, isolated.Status);
        MSTestAssert.AreEqual("recovery-candidate-envelope-invalid", isolated.QuarantineReason);
        MSTestAssert.HasCount(0, harness.Queue.EnqueuedItems);
    }

    [TestMethod]
    [TestCategory("Backend")]
    [TestCategory("Integration")]
    [TestCategory("Recovery")]
    [TestCategory("Transaction")]
    public async Task TryRecoverAsync_QueueFailure_RollsBackCanonicalCheckpointAndSnapshot_ThenRetriesIdempotently()
    {
        await using var database = await SqliteIntegrationTestDatabase.CreateAsync();
        using var harness = await CoordinatorHarness.CreateAsync(
            database,
            includeHealthyCandidate: true,
            failNextQueueWrite: true);
        var before = await database.Users.GetByIdAsync(harness.UserId);
        MSTestAssert.IsNotNull(before);
        var damagedCiphertext = before.EncryptedPayload.ToArray();
        await database.UserSyncFaults.AddAsync(new UserSyncFault
        {
            UserId = harness.UserId,
            Scope = UserSyncFaultScope.LocalCanonical,
            Kind = UserSyncFaultKind.CanonicalRootDecryptFailure,
            Status = UserSyncHealthStatus.AwaitingEvidence,
            AffectedComponent = UserDataBlobKind.All.ToString(),
            KeyEpoch = 1,
            MembershipEpoch = 1,
            BlocksPublishing = true,
            BlocksLogin = true,
            BlocksGarbageCollection = true
        });
        await database.UnitOfWork.SaveChangesAsync();
        database.Db.ChangeTracker.Clear();

        var failed = await harness.Coordinator.TryRecoverAsync(
            harness.UserId,
            harness.Key,
            UserSyncKeyConfidence.AuthenticatedSession,
            UserDataRecoveryTrigger.ManualRetry);

        MSTestAssert.AreEqual(UserDataRecoveryState.Failed, failed.State);
        database.Db.ChangeTracker.Clear();
        var rolledBackUser = await database.Users.GetByIdAsync(harness.UserId);
        MSTestAssert.IsNotNull(rolledBackUser);
        CollectionAssert.AreEqual(damagedCiphertext, rolledBackUser.EncryptedPayload);
        var rolledBackCheckpoint = await database.UserCanonicalCheckpoints.GetAsync(harness.UserId);
        MSTestAssert.IsNotNull(rolledBackCheckpoint);
        MSTestAssert.AreEqual(1L, rolledBackCheckpoint.CheckpointSequence);
        var rolledBackSnapshots = await database.UserSyncSnapshots.ListForUserAsync(harness.UserId);
        MSTestAssert.HasCount(1, rolledBackSnapshots);
        MSTestAssert.AreEqual(UserSyncSnapshotStatus.RecoveryCandidate, rolledBackSnapshots[0].Status);
        MSTestAssert.HasCount(0, harness.Queue.EnqueuedItems);
        var retryFault = (await database.UserSyncFaults.ListForUserAsync(harness.UserId))
            .Single(fault => fault.Scope == UserSyncFaultScope.LocalCanonical);
        MSTestAssert.AreEqual(UserSyncHealthStatus.AwaitingEvidence, retryFault.Status);
        MSTestAssert.IsTrue(retryFault.NextRecoveryAttemptAtUtc.HasValue);

        harness.QueueControl.FailNextEnqueue = false;
        var recovered = await harness.Coordinator.TryRecoverAsync(
            harness.UserId,
            harness.Key,
            UserSyncKeyConfidence.AuthenticatedSession,
            UserDataRecoveryTrigger.ManualRetry);

        MSTestAssert.AreEqual(UserDataRecoveryState.Recovered, recovered.State);
        MSTestAssert.HasCount(1, harness.Queue.EnqueuedItems);
    }

    [TestMethod]
    [TestCategory("Backend")]
    [TestCategory("Integration")]
    [TestCategory("Recovery")]
    [TestCategory("Transaction")]
    public async Task TryRecoverAsync_ActivationFailureAfterCommit_RemainsRecoveredAndDurable()
    {
        await using var database = await SqliteIntegrationTestDatabase.CreateAsync();
        using var harness = await CoordinatorHarness.CreateAsync(
            database,
            includeHealthyCandidate: true,
            failNextActivation: true);

        var result = await harness.Coordinator.TryRecoverAsync(
            harness.UserId,
            harness.Key,
            UserSyncKeyConfidence.AuthenticatedSession,
            UserDataRecoveryTrigger.ManualRetry);

        MSTestAssert.AreEqual(UserDataRecoveryState.Recovered, result.State);
        MSTestAssert.HasCount(1, harness.Queue.EnqueuedItems);
        database.Db.ChangeTracker.Clear();
        var recovered = await database.Users.GetByIdAsync(harness.UserId);
        MSTestAssert.IsNotNull(recovered);
        using var verification = await harness.Verification.VerifyCanonicalAsync(
            recovered,
            harness.Key,
            UserSyncKeyConfidence.ExplicitlyTrusted);
        MSTestAssert.IsTrue(verification.IsHealthy);
        MSTestAssert.IsTrue((await database.UserSyncSnapshots.ListForUserAsync(harness.UserId))
            .Any(snapshot => snapshot.Status == UserSyncSnapshotStatus.LocalPublished));
    }

    [TestMethod]
    [TestCategory("Backend")]
    [TestCategory("Integration")]
    [TestCategory("Recovery")]
    public async Task TryRecoverAsync_SameRevisionFork_RemainsTerminal()
    {
        await using var database = await SqliteIntegrationTestDatabase.CreateAsync();
        using var harness = await CoordinatorHarness.CreateAsync(database, includeHealthyCandidate: true);
        await database.UserSyncFaults.AddAsync(new UserSyncFault
        {
            UserId = harness.UserId,
            Scope = UserSyncFaultScope.SnapshotFork,
            Kind = UserSyncFaultKind.SameRevisionFork,
            Status = UserSyncHealthStatus.TerminalConflict,
            AffectedComponent = "snapshot-envelope",
            OriginDeviceId = Guid.NewGuid(),
            OriginInstanceId = Guid.NewGuid(),
            KeyEpoch = 1,
            MembershipEpoch = 1,
            OriginRevision = 5,
            BlocksPublishing = true,
            BlocksMerge = true,
            BlocksLogin = true,
            BlocksGarbageCollection = true
        });
        await database.UnitOfWork.SaveChangesAsync();

        var result = await harness.Coordinator.TryRecoverAsync(
            harness.UserId,
            harness.Key,
            UserSyncKeyConfidence.AuthenticatedSession,
            UserDataRecoveryTrigger.ManualRetry);

        MSTestAssert.AreEqual(UserDataRecoveryState.TerminalFork, result.State);
        MSTestAssert.HasCount(0, harness.Queue.EnqueuedItems);
    }


}
