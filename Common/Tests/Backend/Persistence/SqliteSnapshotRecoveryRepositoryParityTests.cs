using Microsoft.VisualStudio.TestTools.UnitTesting;
using PasswordManagerLocal.Common.Backend.Abstractions.Persistence;
using PasswordManagerLocal.Common.Backend.Models;
using PasswordManagerLocal.Common.Backend.Repositories.Sqlite;
using PasswordManagerLocal.Common.Backend.Services;
using PasswordManagerLocal.Common.Tests.TestInfrastructure;

namespace PasswordManagerLocal.Common.Tests.Backend.Persistence;

[TestClass]
[TestCategory("Backend")]
[TestCategory("Integration")]
public sealed class SqliteSnapshotRecoveryRepositoryParityTests
{
    [TestMethod]
    public async Task SnapshotQueries_KeepExactRevisionAndQuarantineSemantics()
    {
        await using var baseline = await SqliteRepositoryTestDatabase.CreateAsync();
        var user = await AddUserAsync(baseline);
        using var session = baseline.OpenSession();
        using var otherSession = baseline.OpenSession();
        var snapshots = new SqliteUserSyncSnapshotRepository(session);
        var other = new SqliteUserSyncSnapshotRepository(otherSession);
        var pending = Snapshot(user, UserSyncSnapshotStatus.Pending);
        var candidate = Snapshot(user, UserSyncSnapshotStatus.RecoveryCandidate);
        candidate.MembershipEpoch++;
        var fork = Snapshot(user, UserSyncSnapshotStatus.IsolatedFork);
        await snapshots.AddAsync(pending);
        await snapshots.AddAsync(candidate);
        await snapshots.AddAsync(fork);
        await session.SaveChangesAsync();

        Assert.AreEqual(pending.Id, (await snapshots.ListPendingAsync(user.UId, user.KeyEpoch, user.MembershipEpoch)).Single().Id);
        Assert.AreEqual(2, (await snapshots.ListPendingForKeyEpochAsync(user.UId, user.KeyEpoch)).Count);
        CollectionAssert.AreEqual(
            new[] { pending, candidate }.OrderBy(row => row.OriginDeviceId).ThenBy(row => row.OriginInstanceId)
                .Select(row => row.Id).ToArray(),
            (await snapshots.ListRecoveryEvidenceAsync(user.UId, user.KeyEpoch)).Select(row => row.Id).ToArray());
        Assert.IsTrue(await snapshots.HasIsolatedForkAsync(user.UId, user.KeyEpoch, user.MembershipEpoch));
        Assert.IsNull(await snapshots.GetLatestLocalAsync(user.UId, pending.OriginDeviceId, pending.OriginInstanceId, user.KeyEpoch));

        var stale = (await snapshots.GetExactAsync(user.UId, pending.OriginDeviceId, pending.OriginInstanceId, user.KeyEpoch, 1))!;
        var replacement = (await other.GetAsync(user.UId, pending.OriginDeviceId, pending.OriginInstanceId, user.KeyEpoch))!;
        replacement.OriginRevision = 2;
        replacement.SnapshotHash = [0x39];
        replacement.EnvelopePayload = [0x45];
        other.Update(replacement);
        await otherSession.SaveChangesAsync();

        stale.Status = UserSyncSnapshotStatus.IsolatedCorrupt;
        snapshots.Update(stale);
        var conflict = await Assert.ThrowsExactlyAsync<StorageWriteException>(() => session.SaveChangesAsync());
        Assert.AreEqual(StorageWriteException.FailureKind.StaleRow, conflict.Kind);
        Assert.AreEqual(typeof(UserSyncSnapshot), conflict.RowType);
        Assert.IsNull(await snapshots.GetExactAsync(user.UId, pending.OriginDeviceId, pending.OriginInstanceId, user.KeyEpoch, 1));
        var durable = (await snapshots.GetExactAsync(user.UId, pending.OriginDeviceId, pending.OriginInstanceId, user.KeyEpoch, 2))!;
        Assert.AreEqual(UserSyncSnapshotStatus.Pending, durable.Status);
        CollectionAssert.AreEqual(replacement.EnvelopePayload, durable.EnvelopePayload);
    }

    [TestMethod]
    public async Task RevisionKnowledge_ReusesPendingOriginEpochAndRetainsSeparateEpochs()
    {
        await using var baseline = await SqliteRepositoryTestDatabase.CreateAsync();
        var user = await AddUserAsync(baseline);
        using var session = baseline.OpenSession();
        var knowledge = new SqliteUserRevisionKnowledgeRepository(session);
        var first = new UserRevisionKnowledge
        {
            UserId = user.UId, OriginDeviceId = Guid.NewGuid(), OriginInstanceId = Guid.NewGuid(),
            UserKeyEpoch = user.KeyEpoch, HighestMergedRevision = 2
        };
        await knowledge.AddAsync(first);
        var pending = (await knowledge.GetAsync(user.UId, first.OriginDeviceId, first.OriginInstanceId, user.KeyEpoch))!;
        Assert.AreSame(first, pending);
        pending.HighestMergedRevision = 4;
        knowledge.Update(pending);
        await knowledge.AddAsync(new UserRevisionKnowledge
        {
            UserId = user.UId, OriginDeviceId = first.OriginDeviceId, OriginInstanceId = first.OriginInstanceId,
            UserKeyEpoch = user.KeyEpoch + 1, HighestStoredRevision = 1, HighestStoredSnapshotHash = [0x41]
        });
        Assert.AreEqual(2, await session.SaveChangesAsync());

        var epochs = await baseline.UserRevisionKnowledge.ListForOriginAsync(user.UId, first.OriginDeviceId, first.OriginInstanceId);
        CollectionAssert.AreEqual(new[] { user.KeyEpoch, user.KeyEpoch + 1 }, epochs.Select(row => row.UserKeyEpoch).ToArray());
        Assert.AreEqual(4L, epochs[0].HighestMergedRevision);
        Assert.AreEqual(0L, epochs[0].HighestStoredRevision);
        Assert.AreEqual(1L, epochs[1].HighestStoredRevision);
    }

    [TestMethod]
    public async Task FaultQueries_MatchNullableScopeAndPendingRecoveryState()
    {
        await using var baseline = await SqliteRepositoryTestDatabase.CreateAsync();
        var user = await AddUserAsync(baseline);
        using var session = baseline.OpenSession();
        var faults = new SqliteUserSyncFaultRepository(session);
        var service = new UserSyncFaultService(faults);
        var descriptor = new UserSyncFaultDescriptor
        {
            UserId = user.UId,
            Scope = UserSyncFaultScope.LocalCanonical,
            Kind = UserSyncFaultKind.CanonicalCheckpointMismatch,
            Status = UserSyncHealthStatus.AwaitingEvidence,
            BlocksPublishing = true,
            DiagnosticCode = "checkpoint-mismatch"
        };
        var recorded = await service.RecordAsync(descriptor);
        var repeated = await service.RecordAsync(descriptor with { DiagnosticCode = "checkpoint-still-mismatched" });
        Assert.AreSame(recorded, repeated);
        Assert.IsTrue(await faults.HasBlockingPublishingFaultAsync(user.UId));

        var now = new DateTimeOffset(2026, 11, 7, 20, 0, 0, TimeSpan.Zero);
        recorded.NextRecoveryAttemptAtUtc = now.AddMinutes(1);
        faults.Update(recorded);
        Assert.AreEqual(1, await session.SaveChangesAsync());
        Assert.AreEqual(0, (await faults.ListRecoverableLocalCanonicalAsync(now)).Count);
        Assert.AreEqual(recorded.Id, (await faults.ListRecoverableLocalCanonicalAsync(now.AddMinutes(1))).Single().Id);

        await service.MarkLocalCanonicalRecoveredAsync(user.UId);
        Assert.IsFalse(await faults.HasBlockingPublishingFaultAsync(user.UId));
        Assert.IsNull(await faults.GetActiveAsync(user.UId, descriptor.Scope, descriptor.Kind, null, null, null));
        await session.SaveChangesAsync();
        Assert.AreEqual(UserSyncHealthStatus.Recovered, (await baseline.UserSyncFaults.ListForUserAsync(user.UId)).Single().Status);
    }

    private static async Task<User> AddUserAsync(SqliteRepositoryTestDatabase baseline)
    {
        var user = new User { KeyEpoch = 2, MembershipEpoch = 3 };
        user.GenerateIntegrityHash();
        await baseline.Users.AddAsync(user);
        await baseline.UnitOfWork.SaveChangesAsync();
        return user;
    }

    private static UserSyncSnapshot Snapshot(User user, UserSyncSnapshotStatus status) => new()
    {
        UserId = user.UId,
        OriginDeviceId = Guid.NewGuid(),
        OriginInstanceId = Guid.NewGuid(),
        OriginRevision = 1,
        UserKeyEpoch = user.KeyEpoch,
        MembershipEpoch = user.MembershipEpoch,
        SnapshotHash = [0x10],
        EnvelopePayload = [0x20],
        Status = status
    };
}
