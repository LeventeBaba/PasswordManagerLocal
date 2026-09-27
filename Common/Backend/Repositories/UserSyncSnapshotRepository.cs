using Microsoft.EntityFrameworkCore;
using PasswordManagerLocal.Common.Backend.Abstractions.Repositories;
using PasswordManagerLocal.Common.Backend.Models;
using PasswordManagerLocal.Common.Backend.Persistence;

namespace PasswordManagerLocal.Common.Backend.Repositories;

public sealed class UserSyncSnapshotRepository : IUserSyncSnapshotRepository
{
    private readonly DbSet<UserSyncSnapshot> _snapshots;

    public UserSyncSnapshotRepository(AppDbContext context)
    {
        _snapshots = context.UserSyncSnapshots;
    }

    public Task<UserSyncSnapshot?> GetAsync(Guid userId, Guid originDeviceId, Guid originInstanceId, long userKeyEpoch, CancellationToken ct = default) =>
        _snapshots.FirstOrDefaultAsync(snapshot =>
            snapshot.UserId == userId &&
            snapshot.OriginDeviceId == originDeviceId &&
            snapshot.OriginInstanceId == originInstanceId &&
            snapshot.UserKeyEpoch == userKeyEpoch, ct);

    public Task<UserSyncSnapshot?> GetExactAsync(Guid userId, Guid originDeviceId, Guid originInstanceId, long userKeyEpoch, long originRevision, CancellationToken ct = default) =>
        _snapshots.FirstOrDefaultAsync(snapshot =>
            snapshot.UserId == userId &&
            snapshot.OriginDeviceId == originDeviceId &&
            snapshot.OriginInstanceId == originInstanceId &&
            snapshot.UserKeyEpoch == userKeyEpoch &&
            snapshot.OriginRevision == originRevision, ct);

    public async Task<IReadOnlyList<UserSyncSnapshot>> ListPendingAsync(Guid userId, long userKeyEpoch, long membershipEpoch, CancellationToken ct = default) =>
        await _snapshots
            .Where(snapshot =>
                snapshot.UserId == userId &&
                snapshot.UserKeyEpoch == userKeyEpoch &&
                snapshot.MembershipEpoch == membershipEpoch &&
                (snapshot.Status == UserSyncSnapshotStatus.Pending || snapshot.Status == UserSyncSnapshotStatus.RecoveryCandidate))
            .OrderBy(snapshot => snapshot.OriginDeviceId)
            .ThenBy(snapshot => snapshot.OriginInstanceId)
            .ThenBy(snapshot => snapshot.OriginRevision)
            .ToListAsync(ct);

    public async Task<IReadOnlyList<UserSyncSnapshot>> ListPendingForKeyEpochAsync(Guid userId, long userKeyEpoch, CancellationToken ct = default) =>
        await _snapshots.Where(snapshot => snapshot.UserId == userId && snapshot.UserKeyEpoch == userKeyEpoch && (snapshot.Status == UserSyncSnapshotStatus.Pending || snapshot.Status == UserSyncSnapshotStatus.RecoveryCandidate))
            .OrderBy(snapshot => snapshot.MembershipEpoch).ThenBy(snapshot => snapshot.OriginDeviceId).ThenBy(snapshot => snapshot.OriginInstanceId).ThenBy(snapshot => snapshot.OriginRevision)
            .ToListAsync(ct);

    public async Task<IReadOnlyList<UserSyncSnapshot>> ListForUserAsync(Guid userId, CancellationToken ct = default) =>
        await _snapshots
            .Where(snapshot => snapshot.UserId == userId)
            .OrderBy(snapshot => snapshot.UserKeyEpoch)
            .ThenBy(snapshot => snapshot.OriginDeviceId)
            .ThenBy(snapshot => snapshot.OriginInstanceId)
            .ThenBy(snapshot => snapshot.OriginRevision)
            .ToListAsync(ct);

    public async Task<IReadOnlyList<UserSyncSnapshot>> ListRecoveryEvidenceAsync(
        Guid userId,
        long userKeyEpoch,
        CancellationToken ct = default)
    {
        var rows = await _snapshots
            .Where(snapshot =>
                snapshot.UserId == userId &&
                snapshot.UserKeyEpoch == userKeyEpoch &&
                (snapshot.Status == UserSyncSnapshotStatus.Pending ||
                 snapshot.Status == UserSyncSnapshotStatus.RecoveryCandidate ||
                 snapshot.Status == UserSyncSnapshotStatus.MergedReceipt ||
                 snapshot.Status == UserSyncSnapshotStatus.LocalPublished))
            .ToListAsync(ct);

        return rows
            .OrderBy(snapshot => snapshot.OriginDeviceId)
            .ThenBy(snapshot => snapshot.OriginInstanceId)
            .ThenBy(snapshot => snapshot.OriginRevision)
            .ThenBy(snapshot => Convert.ToHexString(snapshot.SnapshotHash), StringComparer.Ordinal)
            .ToArray();
    }

    public Task<bool> HasQuarantinedAsync(Guid userId, long userKeyEpoch, long membershipEpoch, CancellationToken ct = default) =>
        _snapshots.AnyAsync(snapshot =>
            snapshot.UserId == userId &&
            snapshot.UserKeyEpoch == userKeyEpoch &&
            snapshot.MembershipEpoch == membershipEpoch &&
            snapshot.Status == UserSyncSnapshotStatus.Quarantined, ct);

    public Task<UserSyncSnapshot?> GetLatestLocalAsync(Guid userId, Guid originDeviceId, Guid originInstanceId, long userKeyEpoch, CancellationToken ct = default) =>
        _snapshots.FirstOrDefaultAsync(snapshot =>
            snapshot.UserId == userId &&
            snapshot.OriginDeviceId == originDeviceId &&
            snapshot.OriginInstanceId == originInstanceId &&
            snapshot.UserKeyEpoch == userKeyEpoch &&
            snapshot.Status == UserSyncSnapshotStatus.LocalPublished, ct);

    public Task AddAsync(UserSyncSnapshot snapshot, CancellationToken ct = default) =>
        _snapshots.AddAsync(snapshot, ct).AsTask();

    public void Update(UserSyncSnapshot snapshot) => _snapshots.Update(snapshot);
    public void Delete(UserSyncSnapshot snapshot) => _snapshots.Remove(snapshot);
    public void DeleteRange(IEnumerable<UserSyncSnapshot> snapshots) => _snapshots.RemoveRange(snapshots);
}
