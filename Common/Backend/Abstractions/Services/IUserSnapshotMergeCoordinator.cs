using PasswordManagerLocal.Common.Backend.Models;
using PasswordManagerLocal.Common.Backend.Security;

namespace PasswordManagerLocal.Common.Backend.Abstractions.Services;

public interface IUserSnapshotMergeCoordinator
{
    Task<bool> TryMergePendingAsync(Guid userId, EncryptionKey key, CancellationToken ct = default);
    Task<bool> TryMergePendingUnderLifecycleAsync(Guid userId, EncryptionKey key, CancellationToken ct = default);

    Task<bool> TryMergePendingAsync(
        Guid userId,
        EncryptionKey key,
        UserSyncKeyConfidence keyConfidence,
        CancellationToken ct = default) => TryMergePendingAsync(userId, key, ct);

    Task<bool> TryMergePendingUnderLifecycleAsync(
        Guid userId,
        EncryptionKey key,
        UserSyncKeyConfidence keyConfidence,
        CancellationToken ct = default) => TryMergePendingUnderLifecycleAsync(userId, key, ct);
}
