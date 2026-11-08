using Microsoft.VisualStudio.TestTools.UnitTesting;
using PasswordManagerLocal.Common.Backend.Abstractions.Persistence;
using PasswordManagerLocal.Common.Backend.Models;
using PasswordManagerLocal.Common.Backend.Repositories.Sqlite;
using PasswordManagerLocal.Common.Tests.TestInfrastructure;

namespace PasswordManagerLocal.Common.Tests.Backend.Persistence;

[TestClass]
[TestCategory("Backend")]
[TestCategory("Integration")]
public sealed class SqliteControlAndMembershipRepositoryParityTests
{
    [TestMethod]
    public async Task ControlTransitions_ReleaseFilteredBaseOnlyAfterQuarantineAndKeepEvidence()
    {
        await using var baseline = await SqliteRepositoryTestDatabase.CreateAsync();
        using var session = baseline.OpenSession();
        var operations = new SqliteUserControlOperationRepository(session);
        var states = new SqliteUserControlStateRepository(session);
        var userId = Guid.NewGuid();
        var first = KeyTransition(userId, UserControlOperationStatus.Applied);
        var competing = KeyTransition(userId, UserControlOperationStatus.StoredPending);
        await operations.AddAsync(first);
        await session.SaveChangesAsync();

        var state = new UserControlState { UserId = userId, LocalOriginInstanceId = first.OriginInstanceId };
        await states.AddAsync(state);
        await operations.AddAsync(competing);
        var conflict = await Assert.ThrowsExactlyAsync<StorageWriteException>(() => session.SaveChangesAsync());
        Assert.AreEqual(StorageWriteException.FailureKind.UniqueKeyConflict, conflict.Kind);
        Assert.AreEqual(typeof(UserControlOperation), conflict.RowType);
        Assert.IsNull(await baseline.UserControlStates.GetAsync(userId));
        session.ClearTrackedChanges();

        await states.AddAsync(state);
        var pending = (await states.GetAsync(userId))!;
        Assert.AreSame(state, pending);
        pending.HasConflict = true;
        pending.ConflictingOperationId = competing.OperationId;
        pending.NextOriginSequence = 2;
        states.Update(pending);
        first.Status = UserControlOperationStatus.Quarantined;
        first.ConflictingOperationHash = competing.OperationHash;
        operations.Update(first);
        await operations.AddAsync(competing);
        Assert.AreEqual(3, await session.SaveChangesAsync());

        var persisted = (await baseline.UserControlStates.GetAsync(userId))!;
        Assert.IsTrue(persisted.HasConflict);
        Assert.AreEqual((Guid?)competing.OperationId, persisted.ConflictingOperationId);
        Assert.AreEqual(2L, persisted.NextOriginSequence);
        Assert.AreEqual(competing.OperationId, (await operations.ListPendingAsync()).Single().OperationId);
        Assert.AreEqual(2, (await operations.ListKeyTransitionsFromAsync(userId, 1)).Count);
        Assert.AreEqual(1L, await operations.GetHighestOriginSequenceAsync(userId, first.OriginDeviceId, first.OriginInstanceId));
        var exact = (await operations.GetByOriginSequenceAsync(userId, first.OriginDeviceId, first.OriginInstanceId, 1))!;
        Assert.AreEqual(UserControlOperationStatus.Quarantined, exact.Status);
        CollectionAssert.AreEqual(competing.OperationHash, exact.ConflictingOperationHash!);
    }

    [TestMethod]
    public async Task Membership_RetainsExactSignedEpochAndExcludesReusedDeviceIdentity()
    {
        await using var baseline = await SqliteRepositoryTestDatabase.CreateAsync();
        using var session = baseline.OpenSession();
        using var otherSession = baseline.OpenSession();
        var devices = new SqliteDeviceRepository(session);
        var memberships = new SqliteUserMembershipAuthorizationRepository(session);
        var otherMemberships = new SqliteUserMembershipAuthorizationRepository(otherSession);
        var userId = Guid.NewGuid();
        var fingerprint = string.Concat(Enumerable.Repeat("AB", 32));
        var device = new Device { SignPublicKey = [0x42], TlsCertFingerprint = fingerprint };
        device.GenerateIntegrityHash();
        await devices.AddAsync(device);

        var first = new UserMembershipAuthorization
        {
            AuthorizationId = Guid.NewGuid(), UserId = userId, DeviceId = device.Id, OriginInstanceId = Guid.NewGuid(),
            StartedMembershipEpoch = 1, MinimumKeyEpoch = 1, IsActive = true,
            SignPublicKey = device.SignPublicKey, SignPublicKeyHash = device.SignPublicKeyHash,
            TlsCertFingerprint = string.Join(":", Enumerable.Repeat("ab", 32))
        };
        await memberships.AddAsync(first);
        await session.SaveChangesAsync();
        Assert.IsTrue(await memberships.HasHistoricalAuthorizationAsync(userId, device.Id));
        var stale = (await otherMemberships.GetByIdAsync(first.AuthorizationId))!;

        var next = new UserMembershipAuthorization
        {
            AuthorizationId = Guid.NewGuid(), UserId = userId, DeviceId = device.Id, OriginInstanceId = Guid.NewGuid(),
            StartedMembershipEpoch = 2, MinimumKeyEpoch = 1, IsActive = true,
            SignPublicKey = device.SignPublicKey, SignPublicKeyHash = device.SignPublicKeyHash,
            TlsCertFingerprint = fingerprint
        };
        first.IsActive = false;
        first.EndedMembershipEpoch = 2;
        first.Version++;
        memberships.Update(first);
        await memberships.AddAsync(next);
        await session.SaveChangesAsync();

        stale.Version++;
        otherMemberships.Update(stale);
        var conflict = await Assert.ThrowsExactlyAsync<StorageWriteException>(() => otherSession.SaveChangesAsync());
        Assert.AreEqual(StorageWriteException.FailureKind.StaleRow, conflict.Kind);
        Assert.AreEqual(first.AuthorizationId,
            (await memberships.GetForSignedEpochAsync(userId, device.Id, first.OriginInstanceId, 1))!.AuthorizationId);
        Assert.IsNull(await memberships.GetForSignedEpochAsync(userId, device.Id, first.OriginInstanceId, 2));
        Assert.AreEqual(next.AuthorizationId, (await memberships.ListActiveForDeviceAsync(userId, device.Id)).Single().AuthorizationId);

        CollectionAssert.AreEqual(new[] { userId }, (await memberships.ListUserIdsForDeviceAsync(device.Id)).ToArray());
        CollectionAssert.AreEqual(new[] { device.Id }, (await memberships.ListDeviceIdsForUsersAsync([userId])).ToArray());
        Assert.IsTrue(await memberships.HasHistoricalAuthorizationAsync(userId, device.Id));
        device.SignPublicKey = [0x43];
        devices.Update(device);
        await session.SaveChangesAsync();

        Assert.IsFalse(await memberships.HasHistoricalAuthorizationAsync(userId, device.Id));
        Assert.AreEqual(0, (await memberships.ListUserIdsForDeviceAsync(device.Id)).Count);
        Assert.AreEqual(0, (await memberships.ListDeviceIdsForUsersAsync([userId])).Count);
        Assert.AreEqual(2, (await baseline.UserMembershipAuthorizations.ListForUserAsync(userId)).Count);
    }

    [TestMethod]
    public async Task EnrollmentAndCutoffs_KeepInstallationAndKeyEpochBoundaries()
    {
        await using var baseline = await SqliteRepositoryTestDatabase.CreateAsync();
        using var session = baseline.OpenSession();
        var commits = new SqliteDeviceEnrollmentCommitRepository(session);
        var cutoffs = new SqliteUserOriginRemovalCutoffRepository(session);
        var commit = new DeviceEnrollmentCommit
        {
            UserId = Guid.NewGuid(), TargetDeviceId = Guid.NewGuid(), TargetOriginInstanceId = Guid.NewGuid()
        };
        var cutoff = new UserOriginRemovalCutoff
        {
            UserId = commit.UserId, DeviceId = commit.TargetDeviceId, OriginInstanceId = commit.TargetOriginInstanceId,
            UserKeyEpoch = 1, HighestAcceptedSnapshotRevision = 4, HighestAcceptedControlSequence = 7,
            RemovalOperationHash = [0x12]
        };
        await commits.AddAsync(commit);
        await cutoffs.AddAsync(cutoff);
        await session.SaveChangesAsync();

        Assert.AreEqual(commit.CommitId,
            (await commits.GetRecoverableAsync(commit.UserId, commit.TargetDeviceId, commit.TargetOriginInstanceId))!.CommitId);
        Assert.IsNull(await commits.GetRecoverableAsync(commit.UserId, commit.TargetDeviceId, Guid.NewGuid()));
        commit.Status = DeviceEnrollmentCommitStatus.Revoked;
        commit.Version++;
        commits.Update(commit);
        await session.SaveChangesAsync();
        Assert.IsNull(await commits.GetRecoverableAsync(commit.UserId, commit.TargetDeviceId, commit.TargetOriginInstanceId));
        Assert.AreEqual(DeviceEnrollmentCommitStatus.Revoked, (await baseline.DeviceEnrollmentCommits.GetByIdAsync(commit.CommitId))!.Status);
        var retained = (await cutoffs.GetAsync(cutoff.UserId, cutoff.DeviceId, cutoff.OriginInstanceId, 1))!;
        Assert.AreEqual(7L, retained.HighestAcceptedControlSequence);
        Assert.AreEqual(4L, retained.HighestAcceptedSnapshotRevision);
        Assert.IsNull(await cutoffs.GetAsync(cutoff.UserId, cutoff.DeviceId, cutoff.OriginInstanceId, 2));
    }

    [TestMethod]
    public async Task DeletionBarrier_SurvivesAccountDeletionAndRejectsPendingAndLaterWrites()
    {
        await using var baseline = await SqliteRepositoryTestDatabase.CreateAsync();
        using var session = baseline.OpenSession();
        var users = new SqliteUserRepository(session);
        var barriers = new SqliteDeletedUserBarrierRepository(session);
        var operations = new SqliteUserControlOperationRepository(session);
        var user = new User();
        user.GenerateIntegrityHash();
        await users.AddAsync(user);
        await session.SaveChangesAsync();

        var deletion = KeyTransition(user.UId, UserControlOperationStatus.Applied);
        deletion.OperationType = UserControlOperationType.AccountDeletion;
        var now = DateTimeOffset.UtcNow;
        var barrier = new DeletedUserBarrier
        {
            UserId = user.UId, DeletionOperationId = deletion.OperationId, DeletionGeneration = Guid.NewGuid(),
            OriginDeviceId = deletion.OriginDeviceId, OriginInstanceId = deletion.OriginInstanceId,
            OriginSequence = deletion.OriginSequence, KeyEpoch = 1, MembershipEpoch = 1,
            OperationHash = deletion.OperationHash, DeletedAtUtc = now, AppliedAtUtc = now, LastUpdatedAtUtc = now
        };
        await barriers.AddAsync(barrier);
        users.Update(user);
        await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => session.SaveChangesAsync());
        Assert.IsFalse(await barriers.ExistsAsync(user.UId));
        session.ClearTrackedChanges();

        await barriers.AddAsync(barrier);
        await operations.AddAsync(deletion);
        users.Delete(user);
        await session.SaveChangesAsync();
        Assert.IsNull(await users.GetByIdAsync(user.UId));
        Assert.IsTrue(await barriers.ExistsAsync(user.UId));
        Assert.IsTrue(await operations.HasAppliedAccountDeletionAsync());
        CollectionAssert.AreEqual(new[] { user.UId }, (await operations.ListAppliedRevocationUserIdsAsync()).ToArray());

        await users.AddAsync(user);
        await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => session.SaveChangesAsync());
        Assert.AreEqual(deletion.OperationId, (await baseline.DeletedUserBarriers.GetAsync(user.UId))!.DeletionOperationId);
        Assert.IsNull(await users.GetByIdAsync(user.UId));
    }

    private static UserControlOperation KeyTransition(Guid userId, UserControlOperationStatus status) => new()
    {
        OperationId = Guid.NewGuid(), UserId = userId, OperationType = UserControlOperationType.KeyEpochReplacement,
        OriginDeviceId = Guid.NewGuid(), OriginInstanceId = Guid.NewGuid(), OriginSequence = 1,
        PreviousKeyEpoch = 1, ResultingKeyEpoch = 2, PreviousMembershipEpoch = 1, ResultingMembershipEpoch = 1,
        CreatedAtUtc = DateTimeOffset.UtcNow, ReceivedAtUtc = DateTimeOffset.UtcNow, OperationHash = Guid.NewGuid().ToByteArray(),
        Status = status
    };
}
