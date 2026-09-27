using PasswordManagerLocal.Common.Backend.Abstractions.Services;
using PasswordManagerLocal.Common.Backend.Models;
using PasswordManagerLocal.Common.Backend.Security;

namespace PasswordManagerLocal.Common.Tests.Fakes;

public sealed class FakeInteractiveSessionStateService : IInteractiveSessionStateService
{
    private readonly IAuthSessionService? _authSessions;
    private readonly IUserRecoverySessionService? _recoverySessions;

    public FakeInteractiveSessionStateService(
        IAuthSessionService? authSessions = null,
        IUserRecoverySessionService? recoverySessions = null,
        bool isActive = true)
    {
        _authSessions = authSessions;
        _recoverySessions = recoverySessions;
        IsActive = isActive;
    }

    public bool IsActive { get; private set; }
    public int ActivateCalls { get; private set; }
    public int DeactivateCalls { get; private set; }
    public Exception? ActivateFailure { get; set; }
    public List<Guid> InvalidatedCacheUserIds { get; } = [];
    public Dictionary<Guid, EncryptionKey> UserKeys { get; } = [];

    public Task ActivateAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ActivateCalls++;
        if (ActivateFailure is not null)
            throw ActivateFailure;
        if (IsActive)
            throw new InvalidOperationException("Interactive session state is already active.");

        IsActive = true;
        return Task.CompletedTask;
    }

    public Task DeactivateAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        DeactivateCalls++;
        IsActive = false;
        return Task.CompletedTask;
    }

    public IDisposable EnterOperation()
    {
        if (!IsActive)
        {
            throw new InvalidOperationException(
                "Interactive user-data state is unavailable without an active interactive session.");
        }

        return new CancellationTokenSource();
    }

    public T ExecuteRequired<T>(Func<T> operation)
    {
        ArgumentNullException.ThrowIfNull(operation);
        if (!IsActive)
        {
            throw new InvalidOperationException(
                "Interactive user-data state is unavailable without an active interactive session.");
        }

        return operation();
    }

    public bool TryGetUserEncryptionKey(Guid userId, out EncryptionKey? key)
    {
        key = null;
        if (!IsActive || !UserKeys.TryGetValue(userId, out var storedKey))
            return false;

        var raw = storedKey.ExportCopy();
        try
        {
            key = EncryptionKey.FromRaw(raw);
            return true;
        }
        finally
        {
            System.Security.Cryptography.CryptographicOperations.ZeroMemory(raw);
        }
    }

    public Task LogoutUserAsync(
        Guid userId,
        AuthSessionInvalidationReason reason,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (IsActive)
            _authSessions?.LogoutUser(userId, reason);

        return Task.CompletedTask;
    }

    public Task RefreshSyncedUserSessionsAsync(
        User user,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return IsActive && _authSessions is not null
            ? _authSessions.RefreshSyncedUserSessionsAsync(user, cancellationToken)
            : Task.CompletedTask;
    }

    public Task RefreshOrInvalidateUserSessionsAsync(
        User user,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return IsActive && _recoverySessions is not null
            ? _recoverySessions.RefreshOrInvalidateAsync(user, cancellationToken)
            : Task.CompletedTask;
    }

    public Task InvalidateUserCacheAsync(
        Guid userId,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (IsActive)
            InvalidatedCacheUserIds.Add(userId);

        return Task.CompletedTask;
    }
}
