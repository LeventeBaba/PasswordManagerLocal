using PasswordManagerLocal.Common.Backend.Constants;
using PasswordManagerLocal.Common.Backend.Diagnostics;
using PasswordManagerLocal.Common.Backend.Abstractions.Services;
using PasswordManagerLocal.Common.Backend.Exceptions;
using PasswordManagerLocal.Common.Backend.Models;
using PasswordManagerLocal.Common.Backend.Models.Encrypted;
using PasswordManagerLocal.Common.Contracts.Requests;
using PasswordManagerLocal.Common.Backend.Security;
using PasswordManagerLocal.Common.Backend.Utils;
using System.Security.Cryptography;
using System.Text;

namespace PasswordManagerLocal.Common.Backend.Services;

public sealed class UserLoginService : IUserLoginService
{
    private readonly IUserLookupService _userLookup;
    private readonly IUserDataReaderService _userDataReader;
    private readonly IUserDataWriterService _userDataWriter;
    private readonly IRememberMeService _rememberMe;
    private readonly IDeviceIdentityService _identity;
    private readonly IUserSnapshotMergeCoordinator _snapshotMerge;
    private readonly IUserLifecycleCoordinator _lifecycle;
    private readonly ISyncVersionClockService _versionClock;
    private readonly IAuthenticatedSessionIssuer _sessionIssuer;
    private readonly IUserTombstoneGarbageCollector? _garbageCollector;
    private readonly IUserCanonicalHealthService? _canonicalHealth;
    private readonly IUserDataRecoveryCoordinator? _recoveryCoordinator;

    public UserLoginService(
        IUserLookupService userLookup,
        IUserDataReaderService userDataReader,
        IUserDataWriterService userDataWriter,
        IRememberMeService rememberMe,
        IDeviceIdentityService identity,
        IUserSnapshotMergeCoordinator snapshotMerge,
        IUserLifecycleCoordinator lifecycle,
        ISyncVersionClockService versionClock,
        IAuthenticatedSessionIssuer sessionIssuer,
        IUserTombstoneGarbageCollector? garbageCollector = null,
        IUserCanonicalHealthService? canonicalHealth = null,
        IUserDataRecoveryCoordinator? recoveryCoordinator = null)
    {
        _userLookup = userLookup;
        _userDataReader = userDataReader;
        _userDataWriter = userDataWriter;
        _rememberMe = rememberMe;
        _identity = identity;
        _snapshotMerge = snapshotMerge;
        _lifecycle = lifecycle;
        _versionClock = versionClock;
        _sessionIssuer = sessionIssuer;
        _garbageCollector = garbageCollector;
        _canonicalHealth = canonicalHealth;
        _recoveryCoordinator = recoveryCoordinator;
    }

    public async Task<Guid> LoginAsync(LoginRequest request, CancellationToken ct = default)
    {
        if (!request.Validate())
            throw new InvalidInputException();

        var usernameBytes = Encoding.UTF8.GetBytes(request.Username);
        try
        {
            var initialResolution = await _userLookup.ResolveUsernameAsync(usernameBytes, ct);
            if (initialResolution.State != UserLoginIdentityMatchState.Matched || !initialResolution.UserId.HasValue)
            {
                BackendDebugLog.Warning(
                    $"Login username resolution failed. State={initialResolution.State}, " +
                    $"HasProjectedUserId={initialResolution.UserId.HasValue}, " +
                    $"Diagnostic={initialResolution.Diagnostic ?? "<none>"}.",
                    category: "Authentication");
                throw new UserNotFoundException();
            }

            var userId = initialResolution.UserId.Value;
            return await _lifecycle.ExecuteAsync(
                userId,
                token => LoginResolvedUnderLifecycleAsync(request, usernameBytes, userId, token),
                ct);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(usernameBytes);
        }
    }

    private async Task<Guid> LoginResolvedUnderLifecycleAsync(
        LoginRequest request,
        byte[] usernameBytes,
        Guid expectedUserId,
        CancellationToken ct)
    {
        var prePasswordResolution = await _userLookup.ResolveUsernameAsync(usernameBytes, ct);
        if (prePasswordResolution.State != UserLoginIdentityMatchState.Matched ||
            prePasswordResolution.UserId != expectedUserId)
            throw new UsernameChangedDuringLoginException();

        var user = await _userLookup.GetUserByUidAsync(expectedUserId, ct)
                   ?? throw new UserNotFoundException();
        byte[]? authenticatedRecoverySalt = null;
        try
        {
            try
            {
                user.VerifyIntegrity();
            }
            catch (InvalidDataIntegrityException) when (_recoveryCoordinator is not null)
            {
                // A damaged canonical row must not block recovery before the supplied password can
                // be tested against authenticated current-epoch evidence. The salt resolver never
                // trusts the damaged row and returns a value only when all eligible signed evidence
                // agrees on one historically authorized salt.
                authenticatedRecoverySalt = await _recoveryCoordinator.TryResolvePasswordSaltAsync(user.UId, ct);
            }

            var passwordSalt = authenticatedRecoverySalt ?? user.PasswordSalt;
            if (passwordSalt.Length != CryptographyConstants.Sha256HashSizeInBytes)
                throw new UnauthorizedAccessException("Authentication failed.");

            using var key = EncryptionKey.FromPassword(request.Password, passwordSalt);

            await _snapshotMerge.TryMergePendingUnderLifecycleAsync(user.UId, key, UserSyncKeyConfidence.UnconfirmedPassword, ct);
            if (_recoveryCoordinator is not null)
            {
                await _recoveryCoordinator.TryRecoverAsync(
                    user.UId,
                    key,
                    UserSyncKeyConfidence.UnconfirmedPassword,
                    UserDataRecoveryTrigger.Login,
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
                    // Login must remain available when conservative maintenance cannot complete.
                }
            }

            user = await _userLookup.GetAndVerifyUserByUidAsync(user.UId, ct);
            var bundle = await _userDataReader.GetAndVerifyUserDataBundleAsync(user, key, ct);
            try
            {
                UserLoginIdentityMetadataUtil.Verify(user, bundle.GeneralUserData);
                if (_canonicalHealth is not null)
                {
                    var health = await _canonicalHealth.VerifyAsync(
                        user, key, UserSyncKeyConfidence.ExplicitlyTrusted, recordFault: true, ct: ct);
                    if (health.State is not (UserDataVerificationState.Healthy or UserDataVerificationState.CheckpointMissing))
                        throw new UnauthorizedAccessException("The local account copy could not be verified.");
                }

                var postMergeResolution = await _userLookup.ResolveUsernameAsync(usernameBytes, ct);
                if (postMergeResolution.State != UserLoginIdentityMatchState.Matched ||
                    postMergeResolution.UserId != expectedUserId)
                    throw new UsernameChangedDuringLoginException();

                var modifiedBlobs = UserDataBlobKind.None;
                UserDeviceLoginUtil.UpdateCurrentDeviceLastLoginDate(
                    bundle.UserDevicesData,
                    _identity.LocalDeviceId,
                    DateTimeOffset.UtcNow,
                    _versionClock.Next());
                modifiedBlobs |= UserDataBlobKind.Devices;
                _rememberMe.SetRememberMe(user, request.RememberMe, key);
                await _userDataWriter.UpdateUserDataBundleAsync(bundle, user, key, modifiedBlobs, true, ct);

                try
                {
                    var finalResolution = await _userLookup.ResolveUsernameAsync(usernameBytes, ct);
                    if (finalResolution.State != UserLoginIdentityMatchState.Matched ||
                        finalResolution.UserId != expectedUserId)
                        throw new UsernameChangedDuringLoginException();

                    return _sessionIssuer.IssueAuthenticatedSession(user.UId, key, bundle);
                }
                catch (Exception ex)
                {
                    throw new MutationPartiallyCommittedException(
                        "The login state was committed, but the authenticated session could not be completed.",
                        innerException: ex);
                }
            }
            catch
            {
                bundle.Dispose();
                throw;
            }
        }
        finally
        {
            if (authenticatedRecoverySalt is not null)
                CryptographicOperations.ZeroMemory(authenticatedRecoverySalt);
        }
    }
}
