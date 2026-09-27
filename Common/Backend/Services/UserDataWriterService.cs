using PasswordManagerLocal.Common.Backend.Exceptions;
using PasswordManagerLocal.Common.Backend.Abstractions.Persistence;
using PasswordManagerLocal.Common.Backend.Abstractions.Repositories;
using PasswordManagerLocal.Common.Backend.Abstractions.Services;
using PasswordManagerLocal.Common.Backend.Models;
using PasswordManagerLocal.Common.Backend.Models.Encrypted;
using PasswordManagerLocal.Common.Backend.Security;
using PasswordManagerLocal.Common.Backend.Sync;
using PasswordManagerLocal.Common.Backend.Utils;
using System.Security.Cryptography;
using static PasswordManagerLocal.Common.Backend.Utils.DataCodec;
using PasswordManagerLocal.Common.Backend.Sync.Tombstones;

namespace PasswordManagerLocal.Common.Backend.Services;

/// <summary>
/// Encrypts and persists user-data changes and optionally records them in the synchronization queue.
/// </summary>
public sealed class UserDataWriterService : IUserDataWriterService
{
    private readonly IUserRepository _users;
    private readonly IInteractiveUserDataStateAccessor _interactiveState;
    private readonly IUserLookupService _lookup;
    private readonly ISyncChangeQueueService _syncQueue;
    private readonly IUserDataBundleIntegrityService _integrity;
    private readonly IUserDataPersistenceValidator _validator;
    private readonly IUserSyncStateRepository _syncStates;
    private readonly IDeviceIdentityService _identity;
    private readonly IUserLifecycleCoordinator _lifecycle;
    private readonly IUnitOfWork _uow;
    private readonly IUserLoginIdentityProjectionService _loginIdentities;
    private readonly IUserCanonicalHealthService? _canonicalHealth;

    public UserDataWriterService(
        IUserRepository users,
        IInteractiveUserDataStateAccessor interactiveState,
        IUserLookupService lookup,
        ISyncChangeQueueService syncQueue,
        IUserDataBundleIntegrityService integrity,
        IUserDataPersistenceValidator validator,
        IUserSyncStateRepository syncStates,
        IDeviceIdentityService identity,
        IUserLifecycleCoordinator lifecycle,
        IUnitOfWork uow,
        IUserLoginIdentityProjectionService loginIdentities,
        IUserCanonicalHealthService? canonicalHealth = null)
    {
        _users = users;
        _interactiveState = interactiveState;
        _lookup = lookup;
        _syncQueue = syncQueue;
        _integrity = integrity;
        _validator = validator;
        _syncStates = syncStates;
        _identity = identity;
        _lifecycle = lifecycle;
        _uow = uow;
        _loginIdentities = loginIdentities;
        _canonicalHealth = canonicalHealth;
    }

    public async Task AddNewUserAsync(User user, CancellationToken ct = default)
    {
        var now = DateTimeOffset.UtcNow;
        user.LastModifiedAt = now;
        EnsureBlobTimestamps(user, now);
        user.GenerateIntegrityHash();
        await _users.AddAsync(user, ct);
        if (_canonicalHealth is not null)
            await _canonicalHealth.UpdateCheckpointAsync(user, ct);
        await _uow.SaveChangesAsync(ct);
    }

    public Task UpdateUserAsync(User user, CancellationToken ct = default) =>
        UpdateUserAsync(user, false, ct);

    public async Task UpdateUserAsync(User user, bool enqueueSync, CancellationToken ct = default)
    {
        user.LastModifiedAt = DateTimeOffset.UtcNow;
        user.GenerateIntegrityHash();
        _users.Update(user);
        if (_canonicalHealth is not null)
            await _canonicalHealth.UpdateCheckpointAsync(user, ct);

        if (enqueueSync)
        {
            await _syncQueue.EnqueueAsync(new SyncItem
            {
                ModelId = user.UId,
                ModelType = SyncModelType.User,
                ChangeType = SyncChangeType.Updated
            }, ct);
            return;
        }

        await _uow.SaveChangesAsync(ct);
    }

    public Task UpdateSavedKeyOnlyAsync(User user, CancellationToken ct = default) =>
        _users.UpdateSavedKeyAsync(user.UId, user.SavedKey, ct);

    public Task UpdateUserDataAsync(UserData userData, User user, EncryptionKey key, CancellationToken ct = default) =>
        UpdateUserDataAsync(userData, user, key, false, ct);

    public async Task UpdateUserDataAsync(
        UserData userData,
        User user,
        EncryptionKey key,
        bool enqueueSync,
        CancellationToken ct = default)
    {
        _validator.EnsureUserDataCanBePersisted(userData, user);
        userData.GenerateIntegrityHash();
        var newEncryptedPayload = await SerializeCompressEncryptAsync(
            userData,
            key,
            BackendJsonSerializerContext.Default.UserData,
            ct: ct);
        ReplaceEncryptedPayload(
            user.EncryptedPayload,
            newEncryptedPayload,
            value => user.EncryptedPayload = value);
        user.UserDataLastModifiedAt = DateTimeOffset.UtcNow;
        await UpdateUserAsync(user, enqueueSync, ct);
    }

    public Task UpdateUserDataAsync(UserData userData, Guid token, EncryptionKey key, CancellationToken ct = default) =>
        UpdateUserDataAsync(userData, token, key, false, ct);

    public async Task UpdateUserDataAsync(
        UserData userData,
        Guid token,
        EncryptionKey key,
        bool enqueueSync,
        CancellationToken ct = default)
    {
        var user = await _lookup.GetAndVerifyUserAsync(token, ct);
        await UpdateUserDataAsync(userData, user, key, enqueueSync, ct);
    }

    public Task UpdateUserDataAsync(UserData userData, Guid token, CancellationToken ct = default) =>
        UpdateUserDataAsync(userData, token, false, ct);

    public async Task UpdateUserDataAsync(
        UserData userData,
        Guid token,
        bool enqueueSync,
        CancellationToken ct = default)
    {
        using var key = _interactiveState.GetEncryptionKeyFromToken(token);
        await UpdateUserDataAsync(userData, token, key, enqueueSync, ct);
    }

    public Task UpdateUserDataBundleAsync(
        UserDataBundle bundle,
        User user,
        EncryptionKey key,
        UserDataBlobKind modifiedBlobs,
        CancellationToken ct = default) =>
        UpdateUserDataBundleAsync(bundle, user, key, modifiedBlobs, false, ct);

    public Task UpdateUserDataBundleAsync(
        UserDataBundle bundle,
        User user,
        EncryptionKey key,
        UserDataBlobKind modifiedBlobs,
        bool enqueueSync,
        CancellationToken ct = default) =>
        _lifecycle.ExecuteAsync(
            user.UId,
            token => UpdateUserDataBundleUnderLifecycleAsync(bundle, user, key, modifiedBlobs, enqueueSync, token),
            ct);

    private async Task UpdateUserDataBundleUnderLifecycleAsync(
        UserDataBundle bundle,
        User user,
        EncryptionKey key,
        UserDataBlobKind modifiedBlobs,
        bool enqueueSync,
        CancellationToken ct)
    {
        await AssignTombstoneCausalReferencesAsync(bundle, user, modifiedBlobs, ct);
        _validator.EnsureUserDataBundleCanBePersisted(bundle, user);
        await PersistUserDataBundleAsync(
            bundle,
            user,
            key,
            modifiedBlobs,
            forceRewriteAllBlobs: false,
            enqueueSync: enqueueSync,
            preserveLogicalTimestamps: false,
            ct: ct);
    }

    public Task UpdateUserDataBundleAsync(
        UserDataBundle bundle,
        Guid token,
        UserDataBlobKind modifiedBlobs,
        CancellationToken ct = default) =>
        UpdateUserDataBundleAsync(bundle, token, modifiedBlobs, false, ct);

    public async Task UpdateUserDataBundleAsync(
        UserDataBundle bundle,
        Guid token,
        UserDataBlobKind modifiedBlobs,
        bool enqueueSync,
        CancellationToken ct = default)
    {
        var user = await _lookup.GetAndVerifyUserAsync(token, ct);
        using var key = _interactiveState.GetEncryptionKeyFromToken(token);
        await UpdateUserDataBundleAsync(bundle, user, key, modifiedBlobs, enqueueSync, ct);
        try
        {
            _interactiveState.SetUserBlobKeys(token, bundle.UserData);
            _interactiveState.SetUserDataBundle(token, bundle);
        }
        catch (Exception ex) when (enqueueSync)
        {
            throw new MutationPartiallyCommittedException(
                "The user-data mutation was committed, but the interactive cache could not be refreshed.",
                innerException: ex);
        }
    }


    public Task CompactUserDataBundleAsync(
        UserDataBundle bundle,
        User user,
        EncryptionKey key,
        UserDataBlobKind modifiedBlobs,
        CancellationToken ct = default) =>
        _lifecycle.ExecuteAsync(
            user.UId,
            token => CompactUserDataBundleUnderLifecycleAsync(bundle, user, key, modifiedBlobs, token),
            ct);

    private async Task CompactUserDataBundleUnderLifecycleAsync(
        UserDataBundle bundle,
        User user,
        EncryptionKey key,
        UserDataBlobKind modifiedBlobs,
        CancellationToken ct)
    {
        if (modifiedBlobs.HasFlag(UserDataBlobKind.Passwords))
            TombstoneCausalReferenceUtil.Validate(bundle.UserPasswordsData);
        if (modifiedBlobs.HasFlag(UserDataBlobKind.Devices))
            TombstoneCausalReferenceUtil.Validate(bundle.UserDevicesData);

        _validator.EnsureUserDataBundleCanBePersisted(bundle, user);
        await PersistUserDataBundleAsync(
            bundle,
            user,
            key,
            modifiedBlobs,
            forceRewriteAllBlobs: false,
            enqueueSync: false,
            preserveLogicalTimestamps: true,
            ct: ct);
    }

    public async Task ReencryptUserDataBundleWithNewKeysAsync(
        UserDataBundle bundle,
        User user,
        EncryptionKey newUserKey,
        bool enqueueSync,
        CancellationToken ct = default)
    {
        _validator.EnsureUserDataBundleCanBePersisted(bundle, user);
        _integrity.VerifyUntrustedBundle(bundle);
        UserDataKeyUtil.ReplaceUserBlobKeys(bundle.UserData);
        await PersistUserDataBundleAsync(
            bundle,
            user,
            newUserKey,
            UserDataBlobKind.None,
            forceRewriteAllBlobs: true,
            enqueueSync: enqueueSync,
            preserveLogicalTimestamps: false,
            ct: ct);
    }

    private async Task AssignTombstoneCausalReferencesAsync(
        UserDataBundle bundle,
        User user,
        UserDataBlobKind modifiedBlobs,
        CancellationToken ct)
    {
        if (!modifiedBlobs.HasFlag(UserDataBlobKind.Passwords) &&
            !modifiedBlobs.HasFlag(UserDataBlobKind.Devices))
        {
            return;
        }

        var state = await _syncStates.GetAsync(user.UId, ct);
        var isNew = state is null;
        if (state is null)
        {
            state = new UserSyncState
            {
                UserId = user.UId,
                LocalOriginInstanceId = _identity.OriginInstanceId,
                NextOriginRevision = 1,
                LastUpdatedAtUtc = DateTimeOffset.UtcNow
            };
            await _syncStates.AddAsync(state, ct);
        }
        else if (state.LocalOriginInstanceId != _identity.OriginInstanceId)
        {
            state.LocalOriginInstanceId = _identity.OriginInstanceId;
            state.NextOriginRevision = 1;
            state.LastPublishedContentHash = [];
            state.LastUpdatedAtUtc = DateTimeOffset.UtcNow;
            _syncStates.Update(state);
        }

        if (state.NextOriginRevision <= 0)
            throw new InvalidOperationException("The next local user snapshot revision is invalid.");

        var changed = TombstoneCausalReferenceUtil.AssignAndValidate(
            bundle,
            _identity.LocalDeviceId,
            _identity.OriginInstanceId,
            user.KeyEpoch,
            user.MembershipEpoch,
            state.NextOriginRevision,
            modifiedBlobs);
        if (changed != UserDataBlobKind.None && !isNew)
        {
            state.LastUpdatedAtUtc = DateTimeOffset.UtcNow;
            _syncStates.Update(state);
        }
    }

    private async Task PersistUserDataBundleAsync(
        UserDataBundle bundle,
        User user,
        EncryptionKey userKey,
        UserDataBlobKind modifiedBlobs,
        bool forceRewriteAllBlobs,
        bool enqueueSync,
        bool preserveLogicalTimestamps,
        CancellationToken ct)
    {
        _integrity.UpdateModifiedBlobIntegrity(bundle, modifiedBlobs);
        _validator.EnsureUserDataBundleCanBePersisted(bundle, user);

        var rewriteGeneral = forceRewriteAllBlobs || modifiedBlobs.HasFlag(UserDataBlobKind.General);
        var rewritePasswords = forceRewriteAllBlobs || modifiedBlobs.HasFlag(UserDataBlobKind.Passwords);
        var rewriteDevices = forceRewriteAllBlobs || modifiedBlobs.HasFlag(UserDataBlobKind.Devices);

        Task<byte[]>? generalTask = rewriteGeneral
            ? EncryptBlobAsync(
                bundle.GeneralUserData,
                bundle.UserData.GeneralUserDataKey,
                BackendJsonSerializerContext.Default.GeneralUserData,
                ct)
            : null;
        Task<byte[]>? passwordsTask = rewritePasswords
            ? EncryptBlobAsync(
                bundle.UserPasswordsData,
                bundle.UserData.UserPasswordsDataKey,
                BackendJsonSerializerContext.Default.UserPasswordsData,
                ct)
            : null;
        Task<byte[]>? devicesTask = rewriteDevices
            ? EncryptBlobAsync(
                bundle.UserDevicesData,
                bundle.UserData.UserDevicesDataKey,
                BackendJsonSerializerContext.Default.UserDevicesData,
                ct)
            : null;
        var userDataTask = SerializeCompressEncryptAsync(
            bundle.UserData,
            userKey,
            BackendJsonSerializerContext.Default.UserData,
            ct: ct);

        var encryptionTasks = new List<Task<byte[]>> { userDataTask };
        if (generalTask is not null)
            encryptionTasks.Add(generalTask);
        if (passwordsTask is not null)
            encryptionTasks.Add(passwordsTask);
        if (devicesTask is not null)
            encryptionTasks.Add(devicesTask);

        try
        {
            await Task.WhenAll(encryptionTasks);
        }
        catch
        {
            foreach (var task in encryptionTasks)
                ZeroCompletedEncryptionTask(task);
            throw;
        }

        var now = DateTimeOffset.UtcNow;
        if (!preserveLogicalTimestamps && (modifiedBlobs != UserDataBlobKind.None || forceRewriteAllBlobs))
            user.UserDataLastModifiedAt = now;

        if (generalTask is not null)
        {
            if (!preserveLogicalTimestamps)
                user.GeneralUserDataLastModifiedAt = now;
            ReplaceEncryptedPayload(
                user.EncryptedGeneralUserDataPayload,
                await generalTask,
                value => user.EncryptedGeneralUserDataPayload = value);
        }

        if (passwordsTask is not null)
        {
            if (!preserveLogicalTimestamps)
                user.UserPasswordsDataLastModifiedAt = now;
            ReplaceEncryptedPayload(
                user.EncryptedUserPasswordsDataPayload,
                await passwordsTask,
                value => user.EncryptedUserPasswordsDataPayload = value);
        }

        if (devicesTask is not null)
        {
            if (!preserveLogicalTimestamps)
                user.UserDevicesDataLastModifiedAt = now;
            ReplaceEncryptedPayload(
                user.EncryptedUserDevicesDataPayload,
                await devicesTask,
                value => user.EncryptedUserDevicesDataPayload = value);
        }

        ReplaceEncryptedPayload(
            user.EncryptedPayload,
            await userDataTask,
            value => user.EncryptedPayload = value);

        if (rewriteGeneral)
        {
            user.SetGeneralUserDataVersion(bundle.GeneralUserData.Version);
            await _loginIdentities.SetCanonicalAsync(user, bundle.GeneralUserData.Version, ct);
        }

        if (preserveLogicalTimestamps)
        {
            user.GenerateIntegrityHash();
            _users.Update(user);
            if (_canonicalHealth is not null)
                await _canonicalHealth.UpdateCheckpointAsync(user, ct);
            await _uow.SaveChangesAsync(ct);
        }
        else
        {
            await UpdateUserAsync(user, enqueueSync, ct);
        }
    }

    private async Task<byte[]> EncryptBlobAsync<T>(
        T data,
        byte[] rawKey,
        System.Text.Json.Serialization.Metadata.JsonTypeInfo<T> typeInfo,
        CancellationToken ct) where T : class
    {
        using var key = EncryptionKey.FromRaw(rawKey);
        return await SerializeCompressEncryptAsync(data, key, typeInfo, ct: ct);
    }

    private void ZeroCompletedEncryptionTask(Task<byte[]> task)
    {
        if (task.Status == TaskStatus.RanToCompletion)
            CryptographicOperations.ZeroMemory(task.Result);
    }

    private void ReplaceEncryptedPayload(
        byte[] currentPayload,
        byte[] replacementPayload,
        Action<byte[]> assignReplacement)
    {
        // User ciphertext columns are EF Core concurrency tokens. Do not mutate the tracked
        // current byte[] in place: EF's OriginalValue may reference that same instance, and
        // zeroing it would make the generated concurrency predicate use zeroed bytes. The old
        // value is encrypted data and can be released normally after the successful update.
        _ = currentPayload;
        assignReplacement(replacementPayload);
    }

    private void EnsureBlobTimestamps(User user, DateTimeOffset value)
    {
        if (user.UserDataLastModifiedAt == default)
            user.UserDataLastModifiedAt = value;
        if (user.GeneralUserDataLastModifiedAt == default)
            user.GeneralUserDataLastModifiedAt = value;
        if (user.UserPasswordsDataLastModifiedAt == default)
            user.UserPasswordsDataLastModifiedAt = value;
        if (user.UserDevicesDataLastModifiedAt == default)
            user.UserDevicesDataLastModifiedAt = value;
    }
}
