using PasswordManagerLocal.Common.Backend.Models;
using PasswordManagerLocal.Common.Backend.Models.Encrypted;
using PasswordManagerLocal.Common.Backend.Security;

namespace PasswordManagerLocal.Common.Backend.Abstractions.Services;

public interface IUserDataReaderService
{
    Task<UserData> GetAndVerifyUserDataAsync(User user, EncryptionKey key, CancellationToken ct = default);
    Task<UserData> GetAndVerifyUserDataAsync(User user, Guid token, CancellationToken ct = default);
    Task<UserDataBundle> GetAndVerifyUserDataBundleAsync(User user, EncryptionKey key, CancellationToken ct = default);
    Task<UserDataBundle> GetAndVerifyUserDataBundleAsync(User user, Guid token, CancellationToken ct = default);
    bool TryGetAndVerifyUserDataFromCache(Guid token, out UserData? userData);
    bool TryGetAndVerifyUserDataBundleFromCache(Guid token, out UserDataBundle? bundle);
    Task<UserData> GetLoadAndVerifyUserDataAsync(Guid token, CancellationToken ct = default, User? user = null);
    Task<UserDataBundle> GetLoadAndVerifyUserDataBundleAsync(Guid token, CancellationToken ct = default, User? user = null);
}
