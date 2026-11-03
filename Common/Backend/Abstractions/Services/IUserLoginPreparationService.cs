using PasswordManagerLocal.Common.Backend.Models;
using PasswordManagerLocal.Common.Backend.Models.Encrypted;
using PasswordManagerLocal.Common.Backend.Security;

namespace PasswordManagerLocal.Common.Backend.Abstractions.Services;

public interface IUserLoginPreparationService
{
    // Call under lifecycle ownership, before reading the canonical password salt or saved key.
    // Only the authenticated control-operation handler may resolve pending security work.
    Task ReconcilePendingSecurityOperationsAsync(Guid userId, CancellationToken ct = default);

    // Recovery may inspect a damaged canonical row, but session issuance must require
    // the current verified generation as well as control-plane authorization.
    Task EnsureAuthorizedAsync(Guid userId, CancellationToken ct = default, bool requireCanonicalGeneration = true);

    // Null means that identity reconciliation or canonical recovery must precede login.
    // The caller owns the verified bundle when the local fast path is available.
    Task<UserDataBundle?> TryPrepareLocalAsync(
        User user, EncryptionKey key, UserSyncKeyConfidence confidence, CancellationToken ct = default);
}
