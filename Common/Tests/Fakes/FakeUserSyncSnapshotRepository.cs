using PasswordManagerLocal.Common.Backend.Abstractions.Repositories;
using PasswordManagerLocal.Common.Backend.Models;

namespace PasswordManagerLocal.Common.Tests.Fakes;

public sealed class FakeUserSyncSnapshotRepository : IUserSyncSnapshotRepository
{
    private readonly List<UserSyncSnapshot> _items = [];

    public Task<UserSyncSnapshot?> GetAsync(Guid userId, Guid originDeviceId, Guid originInstanceId, long userKeyEpoch, CancellationToken ct = default) =>
        Task.FromResult(_items.SingleOrDefault(item =>
            item.UserId == userId && item.OriginDeviceId == originDeviceId &&
            item.OriginInstanceId == originInstanceId && item.UserKeyEpoch == userKeyEpoch));

    public Task<UserSyncSnapshot?> GetExactAsync(Guid userId, Guid originDeviceId, Guid originInstanceId, long userKeyEpoch, long originRevision, CancellationToken ct = default) =>
        Task.FromResult(_items.SingleOrDefault(item =>
            item.UserId == userId && item.OriginDeviceId == originDeviceId &&
            item.OriginInstanceId == originInstanceId && item.UserKeyEpoch == userKeyEpoch &&
            item.OriginRevision == originRevision));

    public Task<IReadOnlyList<UserSyncSnapshot>> ListPendingAsync(Guid userId, long userKeyEpoch, long membershipEpoch, CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyList<UserSyncSnapshot>>(_items.Where(item =>
            item.UserId == userId && item.UserKeyEpoch == userKeyEpoch &&
            item.MembershipEpoch == membershipEpoch && (item.Status == UserSyncSnapshotStatus.Pending || item.Status == UserSyncSnapshotStatus.RecoveryCandidate)).ToList());


    public Task<IReadOnlyList<UserSyncSnapshot>> ListPendingForKeyEpochAsync(Guid userId, long userKeyEpoch, CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyList<UserSyncSnapshot>>(_items.Where(item =>
            item.UserId == userId && item.UserKeyEpoch == userKeyEpoch && (item.Status == UserSyncSnapshotStatus.Pending || item.Status == UserSyncSnapshotStatus.RecoveryCandidate)).ToList());

    public Task<IReadOnlyList<UserSyncSnapshot>> ListForUserAsync(Guid userId, CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyList<UserSyncSnapshot>>(_items.Where(item => item.UserId == userId).ToList());

    public Task<IReadOnlyList<UserSyncSnapshot>> ListRecoveryEvidenceAsync(Guid userId, long userKeyEpoch, CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyList<UserSyncSnapshot>>(_items.Where(item =>
            item.UserId == userId && item.UserKeyEpoch == userKeyEpoch &&
            item.Status is UserSyncSnapshotStatus.Pending or UserSyncSnapshotStatus.RecoveryCandidate or
                UserSyncSnapshotStatus.MergedReceipt or UserSyncSnapshotStatus.LocalPublished)
            .OrderBy(item => item.OriginDeviceId)
            .ThenBy(item => item.OriginInstanceId)
            .ThenBy(item => item.OriginRevision)
            .ThenBy(item => Convert.ToHexString(item.SnapshotHash), StringComparer.Ordinal)
            .ToList());

    public Task<bool> HasQuarantinedAsync(Guid userId, long userKeyEpoch, long membershipEpoch, CancellationToken ct = default) =>
        Task.FromResult(_items.Any(item =>
            item.UserId == userId && item.UserKeyEpoch == userKeyEpoch &&
            item.MembershipEpoch == membershipEpoch && item.Status == UserSyncSnapshotStatus.Quarantined));

    public Task<UserSyncSnapshot?> GetLatestLocalAsync(Guid userId, Guid originDeviceId, Guid originInstanceId, long userKeyEpoch, CancellationToken ct = default) =>
        Task.FromResult(_items.Where(item =>
            item.UserId == userId && item.OriginDeviceId == originDeviceId &&
            item.OriginInstanceId == originInstanceId && item.UserKeyEpoch == userKeyEpoch &&
            item.Status == UserSyncSnapshotStatus.LocalPublished)
            .OrderByDescending(item => item.OriginRevision).FirstOrDefault());

    public Task AddAsync(UserSyncSnapshot snapshot, CancellationToken ct = default)
    {
        _items.Add(snapshot);
        return Task.CompletedTask;
    }

    public void Update(UserSyncSnapshot snapshot)
    {
    }

    public void Delete(UserSyncSnapshot snapshot) => _items.Remove(snapshot);

    public void DeleteRange(IEnumerable<UserSyncSnapshot> snapshots)
    {
        foreach (var snapshot in snapshots.ToList())
            _items.Remove(snapshot);
    }
}
