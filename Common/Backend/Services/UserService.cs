using PasswordManagerLocal.Common.Backend.Abstractions.Services;
using PasswordManagerLocal.Common.Backend.Models;
using PasswordManagerLocal.Common.Backend.Models.Encrypted;
using PasswordManagerLocal.Common.Backend.Security;

namespace PasswordManagerLocal.Common.Backend.Services;

/// <summary>
/// Compatibility facade for callers that need the complete user aggregate API.
/// New services should prefer the narrower user-service interfaces.
/// </summary>
public sealed class UserService : IUserService
{
    private readonly IUserSessionService _sessions;
    private readonly IUserLookupService _lookup;
    private readonly IUserDataReaderService _reader;
    private readonly IUserDataWriterService _writer;
    private readonly IUserDeletionService _deletion;

    public UserService(
        IUserSessionService sessions,
        IUserLookupService lookup,
        IUserDataReaderService reader,
        IUserDataWriterService writer,
        IUserDeletionService deletion)
    {
        _sessions = sessions;
        _lookup = lookup;
        _reader = reader;
        _writer = writer;
        _deletion = deletion;
    }

    public Guid GetUidFromToken(Guid token) =>
        _sessions.GetUidFromToken(token);

    public EncryptionKey GetEncryptionKeyFromToken(Guid token) =>
        _sessions.GetEncryptionKeyFromToken(token);

    public Task<User?> GetUserByUidAsync(Guid uid, CancellationToken ct = default) =>
        _lookup.GetUserByUidAsync(uid, ct);

    public Task<User> GetAndVerifyUserByUidAsync(Guid uid, CancellationToken ct = default) =>
        _lookup.GetAndVerifyUserByUidAsync(uid, ct);

    public Task<User> GetAndVerifyUserAsync(Guid token, CancellationToken ct = default) =>
        _lookup.GetAndVerifyUserAsync(token, ct);

    public Task<User?> GetUserByUsernameAsync(byte[] username, CancellationToken ct = default) =>
        _lookup.GetUserByUsernameAsync(username, ct);

    public Task<User> GetAndVerifyUserByUsernameAsync(byte[] username, CancellationToken ct = default) =>
        _lookup.GetAndVerifyUserByUsernameAsync(username, ct);

    public Task<UserData> GetAndVerifyUserDataAsync(User user, EncryptionKey key) =>
        _reader.GetAndVerifyUserDataAsync(user, key);

    public Task<UserData> GetAndVerifyUserDataAsync(User user, Guid token) =>
        _reader.GetAndVerifyUserDataAsync(user, token);

    public Task<UserDataBundle> GetAndVerifyUserDataBundleAsync(
        User user,
        EncryptionKey key,
        CancellationToken ct = default) =>
        _reader.GetAndVerifyUserDataBundleAsync(user, key, ct);

    public Task<UserDataBundle> GetAndVerifyUserDataBundleAsync(
        User user,
        Guid token,
        CancellationToken ct = default) =>
        _reader.GetAndVerifyUserDataBundleAsync(user, token, ct);

    public bool TryGetAndVerifyUserDataFromCache(Guid token, out UserData? userData) =>
        _reader.TryGetAndVerifyUserDataFromCache(token, out userData);

    public bool TryGetAndVerifyUserDataBundleFromCache(Guid token, out UserDataBundle? bundle) =>
        _reader.TryGetAndVerifyUserDataBundleFromCache(token, out bundle);

    public Task<UserData> GetLoadAndVerifyUserDataAsync(
        Guid token,
        CancellationToken ct = default,
        User? user = null) =>
        _reader.GetLoadAndVerifyUserDataAsync(token, ct, user);

    public Task<UserDataBundle> GetLoadAndVerifyUserDataBundleAsync(
        Guid token,
        CancellationToken ct = default,
        User? user = null) =>
        _reader.GetLoadAndVerifyUserDataBundleAsync(token, ct, user);

    public Task<IReadOnlyList<User>> GetAndVerifyRememberMeEnabledUsersAsync(CancellationToken ct = default) =>
        _lookup.GetAndVerifyRememberMeEnabledUsersAsync(ct);

    public Task AddNewUserAsync(User user, CancellationToken ct = default) =>
        _writer.AddNewUserAsync(user, ct);

    public Task UpdateUserAsync(User user, CancellationToken ct = default) =>
        _writer.UpdateUserAsync(user, ct);

    public Task UpdateUserAsync(User user, bool enqueueSync, CancellationToken ct = default) =>
        _writer.UpdateUserAsync(user, enqueueSync, ct);

    public Task UpdateUserDataAsync(
        UserData userData,
        User user,
        EncryptionKey key,
        CancellationToken ct = default) =>
        _writer.UpdateUserDataAsync(userData, user, key, ct);

    public Task UpdateUserDataAsync(
        UserData userData,
        User user,
        EncryptionKey key,
        bool enqueueSync,
        CancellationToken ct = default) =>
        _writer.UpdateUserDataAsync(userData, user, key, enqueueSync, ct);

    public Task UpdateUserDataAsync(
        UserData userData,
        Guid token,
        EncryptionKey key,
        CancellationToken ct = default) =>
        _writer.UpdateUserDataAsync(userData, token, key, ct);

    public Task UpdateUserDataAsync(
        UserData userData,
        Guid token,
        EncryptionKey key,
        bool enqueueSync,
        CancellationToken ct = default) =>
        _writer.UpdateUserDataAsync(userData, token, key, enqueueSync, ct);

    public Task UpdateUserDataAsync(UserData userData, Guid token, CancellationToken ct = default) =>
        _writer.UpdateUserDataAsync(userData, token, ct);

    public Task UpdateUserDataAsync(
        UserData userData,
        Guid token,
        bool enqueueSync,
        CancellationToken ct = default) =>
        _writer.UpdateUserDataAsync(userData, token, enqueueSync, ct);

    public Task UpdateUserDataBundleAsync(
        UserDataBundle bundle,
        User user,
        EncryptionKey key,
        UserDataBlobKind modifiedBlobs,
        CancellationToken ct = default) =>
        _writer.UpdateUserDataBundleAsync(bundle, user, key, modifiedBlobs, ct);

    public Task UpdateUserDataBundleAsync(
        UserDataBundle bundle,
        User user,
        EncryptionKey key,
        UserDataBlobKind modifiedBlobs,
        bool enqueueSync,
        CancellationToken ct = default) =>
        _writer.UpdateUserDataBundleAsync(bundle, user, key, modifiedBlobs, enqueueSync, ct);

    public Task UpdateUserDataBundleAsync(
        UserDataBundle bundle,
        Guid token,
        UserDataBlobKind modifiedBlobs,
        CancellationToken ct = default) =>
        _writer.UpdateUserDataBundleAsync(bundle, token, modifiedBlobs, ct);

    public Task UpdateUserDataBundleAsync(
        UserDataBundle bundle,
        Guid token,
        UserDataBlobKind modifiedBlobs,
        bool enqueueSync,
        CancellationToken ct = default) =>
        _writer.UpdateUserDataBundleAsync(bundle, token, modifiedBlobs, enqueueSync, ct);

    public Task ReencryptUserDataBundleWithNewKeysAsync(
        UserDataBundle bundle,
        User user,
        EncryptionKey newUserKey,
        bool enqueueSync,
        CancellationToken ct = default) =>
        _writer.ReencryptUserDataBundleWithNewKeysAsync(bundle, user, newUserKey, enqueueSync, ct);

    public Task<bool> UserExistsAsync(Guid uid, CancellationToken ct = default) =>
        _lookup.UserExistsAsync(uid, ct);

    public Task DeleteUserAsync(User user, CancellationToken ct = default) =>
        _deletion.DeleteUserAsync(user, ct);

    public Task DeleteUserAsync(User user, bool enqueueSync, CancellationToken ct = default) =>
        _deletion.DeleteUserAsync(user, enqueueSync, ct);

    public Task DeleteUserAsync(Guid uid, CancellationToken ct = default) =>
        _deletion.DeleteUserAsync(uid, ct);

    public Task DeleteUserAsync(Guid uid, bool enqueueSync, CancellationToken ct = default) =>
        _deletion.DeleteUserAsync(uid, enqueueSync, ct);

    public Task DeleteUserByTokenAsync(Guid token, CancellationToken ct = default) =>
        _deletion.DeleteUserByTokenAsync(token, ct);

    public Task DeleteUserByTokenAsync(Guid token, bool enqueueSync, CancellationToken ct = default) =>
        _deletion.DeleteUserByTokenAsync(token, enqueueSync, ct);
}
