using PasswordManagerLocal.Common.Backend.Abstractions.Services;
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

    public UserSessionService(ITokenService tokens, IKeyVaultService keys)
    {
        _tokens = tokens;
        _keys = keys;
    }

    public Guid GetUidFromToken(Guid token)
    {
        if (!_tokens.TryGetUid(token, out var uid))
            throw new InvalidTokenException();

        return uid;
    }

    public EncryptionKey GetEncryptionKeyFromToken(Guid token)
    {
        if (!_keys.TryGetEncryptionKey(token, out var key))
            throw new InvalidTokenException();

        return key;
    }
}
