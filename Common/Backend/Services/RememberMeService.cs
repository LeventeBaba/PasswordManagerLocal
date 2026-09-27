using PasswordManagerLocal.Common.Backend.Abstractions.Repositories;
using PasswordManagerLocal.Common.Backend.Abstractions.Security;
using PasswordManagerLocal.Common.Backend.Abstractions.Services;
using PasswordManagerLocal.Common.Backend.Exceptions;
using PasswordManagerLocal.Common.Backend.Models;
using PasswordManagerLocal.Common.Backend.Models.Encrypted;
using PasswordManagerLocal.Common.Backend.Security;
using PasswordManagerLocal.Common.Backend.Utils;
using System.Security.Cryptography;

namespace PasswordManagerLocal.Common.Backend.Services;

public class RememberMeService : IRememberMeService
{
    private readonly ITokenService _tokens;
    private readonly IKeyVaultService _keys;
    private readonly IKeyProtector _protector;
    private readonly IUserLookupService _lookup;
    private readonly IUserSessionService _sessions;
    private readonly IUserDataWriterService _writer;
    private readonly IUserDataReaderService _reader;
    private readonly IDeviceIdentityService _identity;
    private readonly IUserSnapshotMergeCoordinator _snapshotMerge;
    private readonly ISyncVersionClockService _versionClock;
    private readonly IUserTombstoneGarbageCollector? _garbageCollector;
    private readonly IUserCanonicalHealthService? _canonicalHealth;
    private readonly IUserDataRecoveryCoordinator? _recoveryCoordinator;
    private readonly IUserRepository? _users;

    public RememberMeService(
        ITokenService tokens,
        IKeyVaultService keys,
        IKeyProtector protector,
        IUserLookupService lookup,
        IUserSessionService sessions,
        IUserDataWriterService writer,
        IUserDataReaderService reader,
        IDeviceIdentityService identity,
        IUserSnapshotMergeCoordinator snapshotMerge,
        ISyncVersionClockService versionClock,
        IUserTombstoneGarbageCollector? garbageCollector = null,
        IUserCanonicalHealthService? canonicalHealth = null,
        IUserDataRecoveryCoordinator? recoveryCoordinator = null,
        IUserRepository? users = null)
    {
        _tokens = tokens;
        _keys = keys;
        _protector = protector;
        _lookup = lookup;
        _sessions = sessions;
        _writer = writer;
        _reader = reader;
        _identity = identity;
        _snapshotMerge = snapshotMerge;
        _versionClock = versionClock;
        _garbageCollector = garbageCollector;
        _canonicalHealth = canonicalHealth;
        _recoveryCoordinator = recoveryCoordinator;
        _users = users;
    }



    public async Task<IReadOnlyList<Guid>> RestoreRememberedSessionsAsync(CancellationToken ct = default)
    {
        var initializedTokens = new List<Guid>();

        // SavedKey is local-only and excluded from the canonical integrity commitment. Loading
        // raw rows here lets an independently unprotected Remember Me key repair a damaged
        // canonical row instead of being blocked by pre-recovery integrity verification.
        var usersEnabledRM = _users is null
            ? await _lookup.GetAndVerifyRememberMeEnabledUsersAsync(ct)
            : await _users.GetAllRememberMeEnabledUsersAsync(ct);
        if (usersEnabledRM.Count == 0)
            return initializedTokens;

        foreach (var user in usersEnabledRM)
        {
            var token = await TryInitializeRememberedUserAsync(user, ct);
            if (token is not null)
                initializedTokens.Add(token.Value);
        }

        return initializedTokens;
    }


    public async Task<Guid> InitializeRememberMeSessionAsync(Guid userId, CancellationToken ct = default)
    {
        var user = _users is null
            ? await _lookup.GetAndVerifyUserByUidAsync(userId, ct)
            : await _users.GetByIdAsync(userId, ct) ?? throw new UserNotFoundException();
        var token = await TryInitializeRememberedUserAsync(user, ct);

        if (token is null)
            throw new InvalidTokenException();

        return token.Value;
    }


    public async Task SetRememberMeAsync(Guid token, bool rememberMe, CancellationToken ct = default)
    {
        var user = await _lookup.GetAndVerifyUserAsync(token, ct);
        using var key = _sessions.GetEncryptionKeyFromToken(token);

        SetRememberMe(user, rememberMe, key);
        await _writer.UpdateSavedKeyOnlyAsync(user, ct);
    }


    public void SetRememberMe(User user, bool rememberMe, EncryptionKey key)
    {
        var isRememberMeCurrently = user.SavedKey is not null;

        if (!rememberMe)
        {
            if (!isRememberMeCurrently)
                return;

            CryptographicOperations.ZeroMemory(user.SavedKey);
            user.SavedKey = null;
            return;
        }

        var raw = key.ExportCopy();
        try
        {
            user.SavedKey = _protector.Protect(raw);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(raw);
        }
    }


    private async Task<Guid?> TryInitializeRememberedUserAsync(User user, CancellationToken ct)
    {
        if (user.SavedKey is null)
            return null;

        byte[]? rawKey = null;
        EncryptionKey? key = null;
        try
        {
            try
            {
                rawKey = _protector.Unprotect(user.SavedKey);
                key = EncryptionKey.FromRaw(rawKey);
            }
            catch (KeyProtectorUnavailableException)
            {
                throw;
            }
            catch (Exception ex) when (ex is CryptographicException or ArgumentException)
            {
                // Only failure to unprotect or import the local saved key proves that Remember Me
                // material itself is unusable. Recovery/decryption failures later must not erase it.
                await DisableBrokenRememberMeAsync(user, ct);
                return null;
            }

            try
            {
                await _snapshotMerge.TryMergePendingAsync(user.UId, key, UserSyncKeyConfidence.RememberMe, ct);
                if (_recoveryCoordinator is not null)
                {
                    await _recoveryCoordinator.TryRecoverAsync(
                        user.UId,
                        key,
                        UserSyncKeyConfidence.RememberMe,
                        UserDataRecoveryTrigger.RememberMeStartup,
                        ct);
                }
                if (_garbageCollector is not null)
                {
                    try
                    {
                        await _garbageCollector.CollectAsync(user.UId, key, ct);
                    }
                    catch (Exception ex) when (ex is not OperationCanceledException)
                    {
                        // Remember Me unlock remains available when maintenance is conservatively blocked.
                    }
                }

                user = await _lookup.GetAndVerifyUserByUidAsync(user.UId, ct);
                var bundle = await _reader.GetAndVerifyUserDataBundleAsync(user, key, ct);
                try
                {
                    if (_canonicalHealth is not null)
                    {
                        var health = await _canonicalHealth.VerifyAsync(
                            user, key, UserSyncKeyConfidence.RememberMe, recordFault: true, ct: ct);
                        if (health.State is not (UserDataVerificationState.Healthy or UserDataVerificationState.CheckpointMissing))
                            throw new UnauthorizedAccessException("The local account copy could not be verified.");
                    }

                    UserDeviceLoginUtil.UpdateCurrentDeviceLastLoginDate(
                        bundle.UserDevicesData,
                        _identity.LocalDeviceId,
                        DateTimeOffset.UtcNow,
                        _versionClock.Next());
                    await _writer.UpdateUserDataBundleAsync(
                        bundle,
                        user,
                        key,
                        UserDataBlobKind.Devices,
                        true,
                        ct);

                    try
                    {
                        var token = _tokens.Issue(user.UId);
                        _keys.SetUserKey(token, key);
                        _keys.SetUserBlobKeys(token, bundle.UserData);
                        return token;
                    }
                    catch (Exception ex)
                    {
                        throw new MutationPartiallyCommittedException(
                            "The remembered-session state was committed, but the local session could not be initialized.",
                            innerException: ex);
                    }
                }
                catch
                {
                    bundle.Dispose();
                    throw;
                }
            }
            catch (Exception ex) when (ex is UnauthorizedAccessException or
                                           CryptographicException or
                                           ArgumentException or
                                           InvalidDataIntegrityException or
                                           InvalidDataException or
                                           UserNotFoundException)
            {
                // A successfully unprotected key is independently trusted. If canonical recovery
                // lacks healthy evidence, preserve the key for a later candidate/startup retry.
                return null;
            }
        }
        finally
        {
            key?.Dispose();
            if (rawKey is not null)
                CryptographicOperations.ZeroMemory(rawKey);
        }
    }


    private async Task DisableBrokenRememberMeAsync(User user, CancellationToken ct)
    {
        try
        {
            if (user.SavedKey is not null)
                CryptographicOperations.ZeroMemory(user.SavedKey);

            user.SavedKey = null;
            await _writer.UpdateSavedKeyOnlyAsync(user, ct);
        }
        catch
        {
        }
    }
}
