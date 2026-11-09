using System.Security.Cryptography;
using PasswordManagerLocal.Common.Backend.Abstractions.Persistence;
using PasswordManagerLocal.Common.Backend.Abstractions.Repositories;
using PasswordManagerLocal.Common.Backend.Abstractions.Services;

namespace PasswordManagerLocal.Common.Backend.Services;

/// <summary>Removes mutable state while retaining authoritative deletion and membership evidence.</summary>
public sealed class UserAccountDeletionCleanupService(IUserAccountDeletionStore store, IUserRepository users)
    : IUserAccountDeletionCleanupService
{
    public async Task DeleteCanonicalAndPendingStateAsync(Guid userId, CancellationToken ct = default)
    {
        if (userId == Guid.Empty)
            throw new ArgumentException("The deleted user identity is invalid.", nameof(userId));

        await store.DeleteDependentStateAsync(userId, ct);
        var user = await users.GetByIdAsync(userId, ct);
        if (user is null)
            return;

        store.StageUserDelete(user);
        if (user.SavedKey is not null)
            CryptographicOperations.ZeroMemory(user.SavedKey);
        user.SavedKey = null;
        user.ClearEncryptedPayloads();
    }
}
