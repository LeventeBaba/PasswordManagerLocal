using PasswordManagerLocal.Common.Backend.Models;
using PasswordManagerLocal.Common.Backend.Security;

namespace PasswordManagerLocal.Common.Backend.Abstractions.Services;

public interface IInteractiveSessionStateService
{
    bool IsActive { get; }

    Task ActivateAsync(CancellationToken cancellationToken = default);
    Task DeactivateAsync(CancellationToken cancellationToken = default);
    IDisposable EnterOperation();
    T ExecuteRequired<T>(Func<T> operation);
    bool TryGetUserEncryptionKey(Guid userId, out EncryptionKey? key);
    Task LogoutUserAsync(
        Guid userId,
        AuthSessionInvalidationReason reason,
        CancellationToken cancellationToken = default);
    Task RefreshSyncedUserSessionsAsync(
        User user,
        CancellationToken cancellationToken = default);
    Task RefreshOrInvalidateUserSessionsAsync(
        User user,
        CancellationToken cancellationToken = default);
    Task InvalidateUserCacheAsync(
        Guid userId,
        CancellationToken cancellationToken = default);
}
