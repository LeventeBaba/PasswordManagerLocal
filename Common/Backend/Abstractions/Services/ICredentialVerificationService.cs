using PasswordManagerLocal.Common.Backend.Security;

namespace PasswordManagerLocal.Common.Backend.Abstractions.Services;

public interface ICredentialVerificationService
{
    bool IsPasswordValid(Guid token, byte[] password, byte[] salt);
    bool TryGetActiveUserEncryptionKey(Guid uid, out EncryptionKey? key);
}
