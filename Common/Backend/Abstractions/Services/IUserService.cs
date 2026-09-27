using PasswordManagerLocal.Common.Backend.Models;
using PasswordManagerLocal.Common.Backend.Models.Encrypted;
using PasswordManagerLocal.Common.Backend.Security;

namespace PasswordManagerLocal.Common.Backend.Abstractions.Services;

public interface IUserService
{
    Guid GetUidFromToken(Guid token);
    EncryptionKey GetEncryptionKeyFromToken(Guid token);

    Task<User?> GetUserByUidAsync(Guid uid, CancellationToken ct = default);
    Task<User> GetAndVerifyUserByUidAsync(Guid uid, CancellationToken ct = default);
    Task<User> GetAndVerifyUserAsync(Guid token, CancellationToken ct = default);

    Task<User?> GetUserByUsernameAsync(byte[] username, CancellationToken ct = default);
    Task<User> GetAndVerifyUserByUsernameAsync(byte[] username, CancellationToken ct = default);

    Task<UserData> GetAndVerifyUserDataAsync(User user, EncryptionKey key);
    Task<UserData> GetAndVerifyUserDataAsync(User user, Guid token);
    Task<UserDataBundle> GetAndVerifyUserDataBundleAsync(User user, EncryptionKey key, CancellationToken ct = default);
    Task<UserDataBundle> GetAndVerifyUserDataBundleAsync(User user, Guid token, CancellationToken ct = default);
    bool TryGetAndVerifyUserDataFromCache(Guid token, out UserData? userData);
    bool TryGetAndVerifyUserDataBundleFromCache(Guid token, out UserDataBundle? bundle);
    Task<UserData> GetLoadAndVerifyUserDataAsync(Guid token, CancellationToken ct = default, User? user = null);
    Task<UserDataBundle> GetLoadAndVerifyUserDataBundleAsync(Guid token, CancellationToken ct = default, User? user = null);

    Task<IReadOnlyList<User>> GetAndVerifyRememberMeEnabledUsersAsync(CancellationToken ct = default);

    Task AddNewUserAsync(User user, CancellationToken ct = default);

    Task UpdateUserAsync(User user, CancellationToken ct = default);
    Task UpdateUserAsync(User user, bool enqueueSync, CancellationToken ct = default);

    Task UpdateUserDataAsync(UserData userData, User user, EncryptionKey key, CancellationToken ct = default);
    Task UpdateUserDataAsync(UserData userData, User user, EncryptionKey key, bool enqueueSync, CancellationToken ct = default);
    Task UpdateUserDataAsync(UserData userData, Guid token, EncryptionKey key, CancellationToken ct = default);
    Task UpdateUserDataAsync(UserData userData, Guid token, EncryptionKey key, bool enqueueSync, CancellationToken ct = default);
    Task UpdateUserDataAsync(UserData userData, Guid token, CancellationToken ct = default);
    Task UpdateUserDataAsync(UserData userData, Guid token, bool enqueueSync, CancellationToken ct = default);

    Task UpdateUserDataBundleAsync(UserDataBundle bundle, User user, EncryptionKey key, UserDataBlobKind modifiedBlobs, CancellationToken ct = default);
    Task UpdateUserDataBundleAsync(UserDataBundle bundle, User user, EncryptionKey key, UserDataBlobKind modifiedBlobs, bool enqueueSync, CancellationToken ct = default);
    Task UpdateUserDataBundleAsync(UserDataBundle bundle, Guid token, UserDataBlobKind modifiedBlobs, CancellationToken ct = default);
    Task UpdateUserDataBundleAsync(UserDataBundle bundle, Guid token, UserDataBlobKind modifiedBlobs, bool enqueueSync, CancellationToken ct = default);
    Task ReencryptUserDataBundleWithNewKeysAsync(UserDataBundle bundle, User user, EncryptionKey newUserKey, bool enqueueSync, CancellationToken ct = default);

    Task<bool> UserExistsAsync(Guid uid, CancellationToken ct = default);

    Task DeleteUserAsync(User user, CancellationToken ct = default);
    Task DeleteUserAsync(User user, bool enqueueSync, CancellationToken ct = default);
    Task DeleteUserAsync(Guid uid, CancellationToken ct = default);
    Task DeleteUserAsync(Guid uid, bool enqueueSync, CancellationToken ct = default);
    Task DeleteUserByTokenAsync(Guid token, CancellationToken ct = default);
    Task DeleteUserByTokenAsync(Guid token, bool enqueueSync, CancellationToken ct = default);
}
