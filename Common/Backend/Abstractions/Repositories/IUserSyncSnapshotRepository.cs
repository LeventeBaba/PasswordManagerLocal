using PasswordManagerLocal.Common.Backend.Models;

namespace PasswordManagerLocal.Common.Backend.Abstractions.Repositories;

public interface IUserSyncSnapshotRepository
{
    Task<UserSyncSnapshot?> GetAsync(Guid userId, Guid originDeviceId, Guid originInstanceId, long userKeyEpoch, CancellationToken ct = default);
    Task<UserSyncSnapshot?> GetExactAsync(Guid userId, Guid originDeviceId, Guid originInstanceId, long userKeyEpoch, long originRevision, CancellationToken ct = default);
    Task<IReadOnlyList<UserSyncSnapshot>> ListPendingAsync(Guid userId, long userKeyEpoch, long membershipEpoch, CancellationToken ct = default);
    Task<IReadOnlyList<UserSyncSnapshot>> ListPendingForKeyEpochAsync(Guid userId, long userKeyEpoch, CancellationToken ct = default);
    Task<IReadOnlyList<UserSyncSnapshot>> ListForUserAsync(Guid userId, CancellationToken ct = default);
    Task<IReadOnlyList<UserSyncSnapshot>> ListRecoveryEvidenceAsync(Guid userId, long userKeyEpoch, CancellationToken ct = default);
    Task<bool> HasQuarantinedAsync(Guid userId, long userKeyEpoch, long membershipEpoch, CancellationToken ct = default);
    Task<UserSyncSnapshot?> GetLatestLocalAsync(Guid userId, Guid originDeviceId, Guid originInstanceId, long userKeyEpoch, CancellationToken ct = default);
    Task AddAsync(UserSyncSnapshot snapshot, CancellationToken ct = default);
    void Update(UserSyncSnapshot snapshot);
    void Delete(UserSyncSnapshot snapshot);
    void DeleteRange(IEnumerable<UserSyncSnapshot> snapshots);
}
