using PasswordManagerLocal.Common.Backend.Models.Encrypted;
using PasswordManagerLocal.Common.Backend.Security;

namespace PasswordManagerLocal.Common.Backend.Abstractions.Services;

public interface IInteractiveUserDataStateAccessor
{
    Guid GetUserIdFromToken(Guid token);
    EncryptionKey GetEncryptionKeyFromToken(Guid token);
    bool TryGetUserDataBundle(Guid token, out UserDataBundle? value);
    void InvalidateToken(Guid token);
}
