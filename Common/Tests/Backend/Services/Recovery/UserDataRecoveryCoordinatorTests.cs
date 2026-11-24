using Microsoft.VisualStudio.TestTools.UnitTesting;
using PasswordManagerLocal.Common.Backend.Models;
using PasswordManagerLocal.Common.Backend.Repositories;
using PasswordManagerLocal.Common.Backend.Sync;
using PasswordManagerLocal.Common.Tests.TestInfrastructure;
using PasswordManagerLocal.Common.Tests.TestInfrastructure.Services.Fixtures;

namespace PasswordManagerLocal.Common.Tests.Backend.Services;

public sealed partial class UserDataRecoveryCoordinatorTests
{
    [TestMethod]
    [TestCategory("Backend")]
    [TestCategory("Integration")]
    [TestCategory("Recovery")]
    [TestCategory("Integration")]
    public async Task ConcurrentRecovery_WaitingAttemptReprovesHealth_WithoutPublishingTwice()
    {
        await using var database = await SqliteRepositoryTestDatabase.CreateAsync();
        using var harness = await CoordinatorHarness.CreateAsync(database, includeHealthyCandidate: true);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        harness.QueueControl.BeforeEnqueueAsync = async ct =>
        {
            entered.TrySetResult();
            await release.Task.WaitAsync(ct);
        };
        var first = harness.Coordinator.TryRecoverAsync(harness.UserId, harness.Key,
            UserSyncKeyConfidence.AuthenticatedSession, UserDataRecoveryTrigger.ManualRetry);
        Task<UserDataRecoveryResult>? second = null;
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            second = harness.Coordinator.TryRecoverAsync(harness.UserId, harness.Key,
                UserSyncKeyConfidence.AuthenticatedSession, UserDataRecoveryTrigger.HealthyCandidateReceived);
            Assert.IsFalse(second.IsCompleted, "The second attempt must wait for the lifecycle owner before reading canonical state.");
            release.TrySetResult();
            Assert.AreEqual(UserDataRecoveryState.Recovered, (await first.WaitAsync(TimeSpan.FromSeconds(5))).State);
            Assert.AreEqual(UserDataRecoveryState.NothingToRecover, (await second.WaitAsync(TimeSpan.FromSeconds(5))).State);
            Assert.HasCount(1, harness.Queue.EnqueuedItems);
            Assert.AreEqual(1, harness.Activation.ActivatePendingSyncsCalls);
            using var readback = database.OpenSession();
            var checkpoint = await new UserCanonicalCheckpointRepository(readback).GetAsync(harness.UserId);
            Assert.IsNotNull(checkpoint);
            Assert.AreEqual(2L, checkpoint.CheckpointSequence);
            var snapshots = await new UserSyncSnapshotRepository(readback).ListForUserAsync(harness.UserId);
            Assert.AreEqual(1, snapshots.Count(row => row.Status == UserSyncSnapshotStatus.LocalPublished));
        }
        finally
        {
            release.TrySetResult();
            await first;
            if (second is not null) await second;
        }
    }

    [TestMethod]
    [TestCategory("Backend")]
    [TestCategory("Integration")]
    [TestCategory("Recovery")]
    [TestCategory("Transaction")]
    public async Task CancellationBeforeCommit_RollsBackCanonicalCheckpointProjectionAndEvidence_ThenAllowsRetry()
    {
        await using var database = await SqliteRepositoryTestDatabase.CreateAsync();
        using var harness = await CoordinatorHarness.CreateAsync(database, includeHealthyCandidate: true);
        var before = (await database.Users.GetByIdAsync(harness.UserId))!;
        var damagedPayload = before.EncryptedPayload.ToArray();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var cancellation = new CancellationTokenSource();
        harness.QueueControl.BeforeEnqueueAsync = async ct =>
        {
            entered.TrySetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, ct);
        };
        var attempt = harness.Coordinator.TryRecoverAsync(harness.UserId, harness.Key,
            UserSyncKeyConfidence.AuthenticatedSession, UserDataRecoveryTrigger.ManualRetry, cancellation.Token);
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            cancellation.Cancel();
            await Assert.ThrowsAsync<OperationCanceledException>(() => attempt);
        }
        finally
        {
            cancellation.Cancel();
            try { await attempt; }
            catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { }
        }
        using (var readback = database.OpenSession())
        {
            var user = await new UserRepository(readback).GetByIdAsNoTrackingAsync(harness.UserId);
            Assert.IsNotNull(user);
            CollectionAssert.AreEqual(damagedPayload, user.EncryptedPayload);
            var checkpoint = await new UserCanonicalCheckpointRepository(readback).GetAsync(harness.UserId);
            Assert.IsNotNull(checkpoint);
            Assert.AreEqual(1L, checkpoint.CheckpointSequence);
            var snapshot = (await new UserSyncSnapshotRepository(readback).ListForUserAsync(harness.UserId)).Single();
            Assert.AreEqual(UserSyncSnapshotStatus.RecoveryCandidate, snapshot.Status);
            Assert.IsNull(await new UserRepository(readback).GetLoginIdentityStateAsync(harness.UserId));
        }
        Assert.HasCount(0, harness.Queue.EnqueuedItems);
        Assert.AreEqual(0, harness.Activation.ActivatePendingSyncsCalls);
        harness.QueueControl.BeforeEnqueueAsync = null;
        Assert.AreEqual(UserDataRecoveryState.Recovered, (await harness.Coordinator.TryRecoverAsync(harness.UserId,
            harness.Key, UserSyncKeyConfidence.AuthenticatedSession, UserDataRecoveryTrigger.ManualRetry)).State);
    }

    [TestMethod]
    [TestCategory("Recovery")]
    [TestCategory("EnrollmentSecurity")]
    public async Task RecoveryWithoutCurrentLocalAuthority_DoesNotReadmitOrPublishTheInstallation()
    {
        await using var database = await SqliteRepositoryTestDatabase.CreateAsync();
        using var harness = await CoordinatorHarness.CreateAsync(database, includeHealthyCandidate: true,
            localInstallationAuthorized: false);
        var before = (await database.Users.GetByIdAsync(harness.UserId))!.EncryptedPayload.ToArray();
        var originalIdentity = harness.LocalIdentity.OriginInstanceId;
        Assert.IsNull(await harness.Coordinator.TryResolvePasswordSaltAsync(harness.UserId));
        foreach (var trigger in new[] { UserDataRecoveryTrigger.Login, UserDataRecoveryTrigger.HealthyCandidateReceived,
                     UserDataRecoveryTrigger.StartupRecoveryScan, UserDataRecoveryTrigger.ManualRetry })
        {
            var result = await harness.Coordinator.TryRecoverAsync(harness.UserId, harness.Key,
                UserSyncKeyConfidence.ExplicitlyTrusted, trigger);
            Assert.AreEqual(UserDataRecoveryState.LocalInstallationUnauthorized, result.State);
        }
        CollectionAssert.AreEqual(before, (await database.Users.GetByIdAsync(harness.UserId))!.EncryptedPayload);
        Assert.HasCount(0, harness.Queue.EnqueuedItems);
        Assert.AreEqual(0, harness.Activation.ActivatePendingSyncsCalls);
        Assert.AreEqual(originalIdentity, harness.LocalIdentity.OriginInstanceId);
        Assert.HasCount(0, await database.UserMembershipAuthorizations.ListForUserAsync(harness.UserId));
        Assert.HasCount(0, await database.UserControlOperations.ListForUserAsync(harness.UserId));
        Assert.HasCount(1, await database.UserSyncSnapshots.ListForUserAsync(harness.UserId));
    }

    [TestMethod]
    [TestCategory("Recovery")]
    [TestCategory("Security")]
    public async Task TerminalLocalCanonicalFault_RemainsStableAcrossHealthyCandidateAndManualRetries()
    {
        await using var database = await SqliteRepositoryTestDatabase.CreateAsync();
        using var harness = await CoordinatorHarness.CreateAsync(database, includeHealthyCandidate: true);
        var fault = new UserSyncFault
        {
            UserId = harness.UserId, Scope = UserSyncFaultScope.LocalCanonical,
            Kind = UserSyncFaultKind.CanonicalPasswordsBlobFailure, Status = UserSyncHealthStatus.TerminalConflict,
            DiagnosticCode = "unique-local-data-conflict", BlocksLogin = true, BlocksPublishing = true,
            ObservedHash = Enumerable.Repeat((byte)0x55, 32).ToArray()
        };
        await database.UserSyncFaults.AddAsync(fault);
        await database.UnitOfWork.SaveChangesAsync();
        var before = (await database.Users.GetByIdAsync(harness.UserId))!.EncryptedPayload.ToArray();
        for (var attempt = 0; attempt < 12; attempt++)
        {
            var result = await harness.Coordinator.TryRecoverAsync(harness.UserId, harness.Key,
                UserSyncKeyConfidence.ExplicitlyTrusted, attempt % 2 == 0
                    ? UserDataRecoveryTrigger.HealthyCandidateReceived : UserDataRecoveryTrigger.ManualRetry);
            Assert.AreEqual(UserDataRecoveryState.ControlPlaneConflict, result.State);
        }
        var after = (await database.UserSyncFaults.ListActiveForUserAsync(harness.UserId)).Single();
        Assert.AreEqual(UserSyncHealthStatus.TerminalConflict, after.Status);
        Assert.AreEqual(0, after.RecoveryAttemptCount);
        Assert.IsNull(after.LastRecoveryAttemptAtUtc);
        Assert.IsNull(after.NextRecoveryAttemptAtUtc);
        CollectionAssert.AreEqual(fault.ObservedHash, after.ObservedHash);
        CollectionAssert.AreEqual(before, (await database.Users.GetByIdAsync(harness.UserId))!.EncryptedPayload);
        Assert.HasCount(0, harness.Queue.EnqueuedItems);
    }

    [TestMethod]
    [TestCategory("Backend")]
    [TestCategory("Integration")]
    [TestCategory("Recovery")]
    [TestCategory("Transaction")]
    public async Task PostCommitActivationFailure_FreshSessionSeesAuthenticatedCheckpointAndLoginProjection()
    {
        await using var database = await SqliteRepositoryTestDatabase.CreateAsync();
        using var harness = await CoordinatorHarness.CreateAsync(database, includeHealthyCandidate: true, failNextActivation: true);
        var result = await harness.Coordinator.TryRecoverAsync(harness.UserId, harness.Key,
            UserSyncKeyConfidence.AuthenticatedSession, UserDataRecoveryTrigger.ManualRetry);
        Assert.AreEqual(UserDataRecoveryState.Recovered, result.State);
        using var readback = database.OpenSession();
        var users = new UserRepository(readback);
        var persisted = await users.GetByIdAsNoTrackingAsync(harness.UserId);
        var checkpoint = await new UserCanonicalCheckpointRepository(readback).GetAsync(harness.UserId);
        Assert.IsNotNull(persisted);
        Assert.IsNotNull(checkpoint);
        UserCanonicalCheckpointUtil.Verify(persisted, checkpoint, harness.LocalIdentity);
        var projection = await users.GetLoginIdentityStateAsync(harness.UserId);
        Assert.IsNotNull(projection);
        Assert.AreEqual(UserLoginIdentityStatus.Active, projection.Status);
        CollectionAssert.AreEqual(persisted.UsernameHash, projection.UsernameHash);
        var publication = (await new UserSyncSnapshotRepository(readback).ListForUserAsync(harness.UserId))
            .Single(row => row.Status == UserSyncSnapshotStatus.LocalPublished);
        Assert.AreEqual(result.RecoveryRevision, publication.OriginRevision);
        Assert.AreEqual(UserDataRecoveryState.NothingToRecover, (await harness.Coordinator.TryRecoverAsync(harness.UserId,
            harness.Key, UserSyncKeyConfidence.AuthenticatedSession, UserDataRecoveryTrigger.ManualRetry)).State);
        Assert.HasCount(1, harness.Queue.EnqueuedItems);
    }
}
