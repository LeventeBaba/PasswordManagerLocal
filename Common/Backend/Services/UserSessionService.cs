using PasswordManagerLocal.Common.Backend.Abstractions.Services;
using PasswordManagerLocal.Common.Backend.Abstractions.Persistence;
using PasswordManagerLocal.Common.Backend.Exceptions;
using PasswordManagerLocal.Common.Backend.Security;

namespace PasswordManagerLocal.Common.Backend.Services;

/// <summary>
/// Resolves the authenticated user identity and encryption key associated with a session token.
/// </summary>
public sealed class UserSessionService : IUserSessionService
{
    private readonly ITokenService _tokens;
    private readonly IKeyVaultService _keys;
    private readonly ICriticalControlIngressStore? _ingress;
    private readonly IUserLifecycleCoordinator? _lifecycle;

    public UserSessionService(ITokenService tokens, IKeyVaultService keys,
        ICriticalControlIngressStore? ingress = null, IUserLifecycleCoordinator? lifecycle = null)
    {
        _tokens = tokens;
        _keys = keys;
        _ingress = ingress;
        _lifecycle = lifecycle;
    }

    public Guid GetUidFromToken(Guid token)
    {
        if (!_tokens.TryGetUid(token, out var uid))
            throw new InvalidTokenException();

        if (_lifecycle?.IsControlOperation(uid) != true) _ingress?.ThrowIfSuspended(uid);
        return uid;
    }

    public EncryptionKey GetEncryptionKeyFromToken(Guid token)
    {
        if (_ingress is not null) _ = GetUidFromToken(token);
        if (!_keys.TryGetEncryptionKey(token, out var key))
            throw new InvalidTokenException();

        return key;
    }
}
