using Microsoft.Extensions.DependencyInjection;
using PasswordManagerLocal.Common.Backend.Abstractions.Services;
using PasswordManagerLocal.Common.Backend.Models.Encrypted;
using PasswordManagerLocal.Common.Backend.Security;

namespace PasswordManagerLocal.Common.Backend.Services;

public sealed class InteractiveUserDataStateAccessor : IInteractiveUserDataStateAccessor
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly IInteractiveSessionStateService _sessionState;

    public InteractiveUserDataStateAccessor(
        IServiceScopeFactory scopeFactory,
        IInteractiveSessionStateService sessionState)
    {
        _scopeFactory = scopeFactory ?? throw new ArgumentNullException(nameof(scopeFactory));
        _sessionState = sessionState ?? throw new ArgumentNullException(nameof(sessionState));
    }

    public Guid GetUserIdFromToken(Guid token) =>
        _sessionState.ExecuteRequired(() =>
        {
            using var scope = _scopeFactory.CreateScope();
            return scope.ServiceProvider
                .GetRequiredService<IUserSessionService>()
                .GetUidFromToken(token);
        });

    public EncryptionKey GetEncryptionKeyFromToken(Guid token) =>
        _sessionState.ExecuteRequired(() =>
        {
            using var scope = _scopeFactory.CreateScope();
            return scope.ServiceProvider
                .GetRequiredService<IUserSessionService>()
                .GetEncryptionKeyFromToken(token);
        });

    public void SetUserBlobKeys(Guid token, UserData userData) =>
        _sessionState.ExecuteRequired(() =>
        {
            using var scope = _scopeFactory.CreateScope();
            scope.ServiceProvider
                .GetRequiredService<IKeyVaultService>()
                .SetUserBlobKeys(token, userData);
            return true;
        });

    public bool TryGetUserData(Guid token, out UserData? value)
    {
        var result = _sessionState.ExecuteRequired(() =>
        {
            using var scope = _scopeFactory.CreateScope();
            var found = scope.ServiceProvider
                .GetRequiredService<IDataCachingService>()
                .TryGetUserData(token, out var foundValue);
            return (Found: found, Value: foundValue);
        });

        value = result.Value;
        return result.Found;
    }

    public bool TryGetUserDataBundle(Guid token, out UserDataBundle? value)
    {
        var result = _sessionState.ExecuteRequired(() =>
        {
            using var scope = _scopeFactory.CreateScope();
            var found = scope.ServiceProvider
                .GetRequiredService<IDataCachingService>()
                .TryGetUserDataBundle(token, out var foundValue);
            return (Found: found, Value: foundValue);
        });

        value = result.Value;
        return result.Found;
    }

    public void SetUserDataBundle(Guid token, UserDataBundle value) =>
        _sessionState.ExecuteRequired(() =>
        {
            using var scope = _scopeFactory.CreateScope();
            scope.ServiceProvider
                .GetRequiredService<IDataCachingService>()
                .SetUserDataBundle(token, value);
            return true;
        });

    public void InvalidateToken(Guid token) =>
        _sessionState.ExecuteRequired(() =>
        {
            using var scope = _scopeFactory.CreateScope();
            scope.ServiceProvider
                .GetRequiredService<IDataCachingService>()
                .InvalidateToken(token);
            return true;
        });
}
