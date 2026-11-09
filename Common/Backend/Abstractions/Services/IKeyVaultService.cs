using PasswordManagerLocal.Common.Backend.Security;

namespace PasswordManagerLocal.Common.Backend.Abstractions.Services;

public interface IKeyVaultService
{
    void SetUserKey(Guid token, EncryptionKey key, DateTimeOffset? expiresAt = null);
    bool RotateUserKey(Guid token, EncryptionKey newKey, DateTimeOffset? newExpiresAt = null);
    bool HasUserKey(Guid token);
    bool TryGetEncryptionKey(Guid token, out EncryptionKey key);
    void InvalidateToken(Guid token);
    int PurgeExpired();
    void ClearAll();
}
