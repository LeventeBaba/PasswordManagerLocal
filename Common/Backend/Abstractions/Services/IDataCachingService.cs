using PasswordManagerLocal.Common.Backend.Models.Encrypted;

namespace PasswordManagerLocal.Common.Backend.Abstractions.Services;

public interface IDataCachingService
{
    bool TryGetUserDataBundle(Guid token, out UserDataBundle? value);
    void SetUserDataBundle(Guid token, UserDataBundle value);

    void InvalidateToken(Guid token);
    void ClearAll();
}
