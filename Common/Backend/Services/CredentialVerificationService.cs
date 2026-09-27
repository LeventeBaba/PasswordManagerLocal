using PasswordManagerLocal.Common.Backend.Abstractions.Services;
using PasswordManagerLocal.Common.Backend.Security;

namespace PasswordManagerLocal.Common.Backend.Services;

public sealed class CredentialVerificationService : ICredentialVerificationService
{
    private readonly IUserSessionService _userSessions;
    private readonly ITokenService _tokens;
    private readonly IKeyVaultService _keys;

    public CredentialVerificationService(
        IUserSessionService userSessions,
        ITokenService tokens,
        IKeyVaultService keys)
    {
        _userSessions = userSessions;
        _tokens = tokens;
        _keys = keys;
    }

    public bool IsPasswordValid(Guid token, byte[] password, byte[] salt)
    {
        using var currentKey = _userSessions.GetEncryptionKeyFromToken(token);
        using var confirmationKey = EncryptionKey.FromPassword(password, salt);
        return currentKey == confirmationKey;
    }


    public bool TryGetActiveUserEncryptionKey(Guid uid, out EncryptionKey? key)
    {
        foreach (var token in _tokens.ListTokensByUid(uid))
        {
            if (_keys.TryGetEncryptionKey(token, out key))
                return true;
        }

        key = null;
        return false;
    }
}
