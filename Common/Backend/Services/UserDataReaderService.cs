using PasswordManagerLocal.Common.Backend.Abstractions.Services;
using PasswordManagerLocal.Common.Backend.Models;
using PasswordManagerLocal.Common.Backend.Models.Encrypted;
using PasswordManagerLocal.Common.Backend.Security;
using PasswordManagerLocal.Common.Backend.Utils;
using static PasswordManagerLocal.Common.Backend.Utils.DataCodec;

namespace PasswordManagerLocal.Common.Backend.Services;

/// <summary>
/// Decrypts, verifies, and caches user-data aggregates for authenticated callers.
/// </summary>
public sealed class UserDataReaderService : IUserDataReaderService
{
    private readonly IInteractiveUserDataStateAccessor _interactiveState;
    private readonly IUserLookupService _users;
    private readonly IUserDataBundleIntegrityService _integrity;
    private readonly ISyncVersionClockService _versionClock;

    public UserDataReaderService(
        IInteractiveUserDataStateAccessor interactiveState,
        IUserLookupService users,
        IUserDataBundleIntegrityService integrity,
        ISyncVersionClockService versionClock)
    {
        _interactiveState = interactiveState;
        _users = users;
        _integrity = integrity;
        _versionClock = versionClock;
    }

    public async Task<UserData> GetAndVerifyUserDataAsync(User user, EncryptionKey key, CancellationToken ct = default)
    {
        var userData = await DecryptUserDataAsync(user, key, ct);
        try
        {
            _integrity.VerifyUserData(userData);
            return userData;
        }
        catch
        {
            userData.Dispose();
            throw;
        }
    }

    public async Task<UserData> GetAndVerifyUserDataAsync(User user, Guid token, CancellationToken ct = default)
    {
        using var key = _interactiveState.GetEncryptionKeyFromToken(token);
        return await GetAndVerifyUserDataAsync(user, key, ct);
    }

    public async Task<UserDataBundle> GetAndVerifyUserDataBundleAsync(User user, EncryptionKey key, CancellationToken ct = default)
    {
        var userData = await DecryptUserDataAsync(user, key, ct);
        try
        {
            _integrity.VerifyUserData(userData);
        }
        catch
        {
            userData.Dispose();
            throw;
        }

        var generalTask = DecryptAndVerifyGeneralUserDataAsync(user, userData, ct);
        var passwordsTask = DecryptAndVerifyUserPasswordsDataAsync(user, userData, ct);
        var devicesTask = DecryptAndVerifyUserDevicesDataAsync(user, userData, ct);

        try
        {
            await Task.WhenAll(generalTask, passwordsTask, devicesTask);

            var bundle = new UserDataBundle
            {
                UserData = userData,
                GeneralUserData = await generalTask,
                UserPasswordsData = await passwordsTask,
                UserDevicesData = await devicesTask
            };

            _integrity.VerifyBundleLinks(bundle);
            _versionClock.Observe(SyncVersionStampTraversal.Enumerate(bundle));
            return bundle;
        }
        catch
        {
            DisposeCompletedTaskResult(generalTask);
            DisposeCompletedTaskResult(passwordsTask);
            DisposeCompletedTaskResult(devicesTask);
            userData.Dispose();
            throw;
        }
    }

    public async Task<UserDataBundle> GetAndVerifyUserDataBundleAsync(User user, Guid token, CancellationToken ct = default)
    {
        using var key = _interactiveState.GetEncryptionKeyFromToken(token);
        var bundle = await GetAndVerifyUserDataBundleAsync(user, key, ct);
        _interactiveState.SetUserBlobKeys(token, bundle.UserData);
        return bundle;
    }

    public bool TryGetAndVerifyUserDataFromCache(Guid token, out UserData? userData)
    {
        if (_interactiveState.TryGetUserData(token, out var foundUserData) && foundUserData is not null)
        {
            _integrity.VerifyUserData(foundUserData);
            userData = foundUserData;
            return true;
        }

        userData = null;
        return false;
    }

    public bool TryGetAndVerifyUserDataBundleFromCache(Guid token, out UserDataBundle? bundle)
    {
        if (_interactiveState.TryGetUserDataBundle(token, out var foundBundle) && foundBundle is not null)
        {
            _integrity.VerifyUntrustedBundle(foundBundle);
            _versionClock.Observe(SyncVersionStampTraversal.Enumerate(foundBundle));
            bundle = foundBundle;
            return true;
        }

        bundle = null;
        return false;
    }

    public async Task<UserData> GetLoadAndVerifyUserDataAsync(Guid token, CancellationToken ct = default, User? user = null)
    {
        var bundle = await GetLoadAndVerifyUserDataBundleAsync(token, ct, user);
        return bundle.UserData;
    }

    public async Task<UserDataBundle> GetLoadAndVerifyUserDataBundleAsync(Guid token, CancellationToken ct = default, User? user = null)
    {
        if (TryGetAndVerifyUserDataBundleFromCache(token, out var foundBundle) && foundBundle is not null)
            return foundBundle;

        user ??= await _users.GetAndVerifyUserAsync(token, ct);

        var bundle = await GetAndVerifyUserDataBundleAsync(user, token, ct);
        _interactiveState.SetUserDataBundle(token, bundle);
        return bundle;
    }

    private async Task<UserData> DecryptUserDataAsync(User user, EncryptionKey key, CancellationToken ct)
    {
        var userData = await DecryptDecompressDeserializeAsync(
            user.EncryptedPayload,
            key,
            BackendJsonSerializerContext.Default.UserData,
            ct: ct);
        if (userData is null)
            throw new UnauthorizedAccessException();

        return userData;
    }

    private async Task<T> DecryptBlobAsync<T>(
        byte[] encryptedBlob,
        byte[] rawKey,
        System.Text.Json.Serialization.Metadata.JsonTypeInfo<T> typeInfo,
        CancellationToken ct) where T : class
    {
        if (encryptedBlob.Length == 0 || rawKey.Length == 0)
            throw new UnauthorizedAccessException();

        using var key = EncryptionKey.FromRaw(rawKey);
        var data = await DecryptDecompressDeserializeAsync(encryptedBlob, key, typeInfo, ct: ct);
        if (data is null)
            throw new UnauthorizedAccessException();

        return data;
    }

    private async Task<T> DecryptAndVerifyBlobAsync<T>(
        byte[] encryptedBlob,
        byte[] rawKey,
        System.Text.Json.Serialization.Metadata.JsonTypeInfo<T> typeInfo,
        Action<T> verifyIntegrity,
        CancellationToken ct) where T : class, IDisposable
    {
        var data = await DecryptBlobAsync(encryptedBlob, rawKey, typeInfo, ct);
        try
        {
            verifyIntegrity(data);
            return data;
        }
        catch
        {
            data.Dispose();
            throw;
        }
    }

    private Task<GeneralUserData> DecryptAndVerifyGeneralUserDataAsync(User user, UserData userData, CancellationToken ct) =>
        DecryptAndVerifyBlobAsync(
            user.EncryptedGeneralUserDataPayload,
            userData.GeneralUserDataKey,
            BackendJsonSerializerContext.Default.GeneralUserData,
            _integrity.VerifyGeneralUserData,
            ct);

    private Task<UserPasswordsData> DecryptAndVerifyUserPasswordsDataAsync(User user, UserData userData, CancellationToken ct) =>
        DecryptAndVerifyBlobAsync(
            user.EncryptedUserPasswordsDataPayload,
            userData.UserPasswordsDataKey,
            BackendJsonSerializerContext.Default.UserPasswordsData,
            _integrity.VerifyUserPasswordsData,
            ct);

    private Task<UserDevicesData> DecryptAndVerifyUserDevicesDataAsync(User user, UserData userData, CancellationToken ct) =>
        DecryptAndVerifyBlobAsync(
            user.EncryptedUserDevicesDataPayload,
            userData.UserDevicesDataKey,
            BackendJsonSerializerContext.Default.UserDevicesData,
            _integrity.VerifyUserDevicesData,
            ct);

    private void DisposeCompletedTaskResult<T>(Task<T> task) where T : IDisposable
    {
        if (task.Status == TaskStatus.RanToCompletion)
            task.Result.Dispose();
    }
}
