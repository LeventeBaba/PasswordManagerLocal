using PasswordManagerLocal.Common.Backend.Models;
using PasswordManagerLocal.Common.Backend.Security;

namespace PasswordManagerLocal.Common.Backend.Abstractions.Services;

public interface IUserSnapshotPublisherService
{
    Task<UserSyncSnapshot> GetOrCreateAsync(User user, CancellationToken ct = default);
    Task<UserSyncSnapshot?> GetLatestAsync(Guid userId, long userKeyEpoch, CancellationToken ct = default);
    Task<UserSyncSnapshot> GetOrCreateAfterRecoveryAsync(
        User user,
        EncryptionKey key,
        UserSyncKeyConfidence keyConfidence,
        CancellationToken ct = default) =>
        GetOrCreateAsync(user, ct);
}
