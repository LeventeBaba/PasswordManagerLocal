using PasswordManagerLocal.Common.Backend.Security;

namespace PasswordManagerLocal.Common.Backend.Abstractions.Services;

public interface IUserSessionService
{
    Guid GetUidFromToken(Guid token);
    EncryptionKey GetEncryptionKeyFromToken(Guid token);
}
