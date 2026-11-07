using PasswordManagerLocal.Common.Backend.Constants;
using PasswordManagerLocal.Common.Backend.Abstractions.Services;
using PasswordManagerLocal.Common.Backend.Abstractions.Repositories;
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
    private static readonly TimeSpan MinimumCredentialFailureTime = TimeSpan.FromMilliseconds(250);
    private static readonly byte[] DummyPasswordSalt = Hashing.GenerateSalt();

    private readonly TimeProvider _timeProvider;
    private readonly IUserLoginIdentityProjectionService _loginIdentities;
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
    private readonly IUserMembershipAuthorizationRepository _membershipAuthorizations;

    private readonly IUserLoginPreparationService? _preparation;
    private readonly IUserPostLoginWorkScheduler? _postLogin;

    public UserLoginService(
        IUserLookupService userLookup,
        IUserLoginIdentityProjectionService loginIdentities,
        IUserDataReaderService userDataReader,
        IUserDataWriterService userDataWriter,
        IRememberMeService rememberMe,
        IDeviceIdentityService identity,
        IUserSnapshotMergeCoordinator snapshotMerge,
        IUserLifecycleCoordinator lifecycle,
        ISyncVersionClockService versionClock,
        IAuthenticatedSessionIssuer sessionIssuer,
        TimeProvider timeProvider,
        IUserTombstoneGarbageCollector? garbageCollector = null,
        IUserCanonicalHealthService? canonicalHealth = null,
        IUserDataRecoveryCoordinator? recoveryCoordinator = null,
        IUserMembershipAuthorizationRepository? membershipAuthorizations = null,
        IUserLoginPreparationService? preparation = null,
        IUserPostLoginWorkScheduler? postLogin = null)
    {
        _preparation = preparation;
        _postLogin = postLogin;
        _userLookup = userLookup;
        _loginIdentities = loginIdentities;
        _userDataReader = userDataReader;
        _userDataWriter = userDataWriter;
        _rememberMe = rememberMe;
        _identity = identity;
        _snapshotMerge = snapshotMerge;
        _lifecycle = lifecycle;
        _versionClock = versionClock;
        _sessionIssuer = sessionIssuer;
        _timeProvider = timeProvider;
        _garbageCollector = garbageCollector;
        _canonicalHealth = canonicalHealth;
        _recoveryCoordinator = recoveryCoordinator;
        _membershipAuthorizations = membershipAuthorizations
            ?? throw new ArgumentNullException(nameof(membershipAuthorizations));
    }

    public async Task<Guid> LoginAsync(LoginRequest request, CancellationToken ct = default)
    {
        if (!request.Validate())
            throw new InvalidInputException();

        var startedAt = _timeProvider.GetTimestamp();
        var passwordDerivationStarted = false;
        try
        {
            return await ResolveAndLoginAsync(request, DerivePasswordKey, ct);
        }
        catch (Exception ex) when (ex is UserNotFoundException or UnauthorizedAccessException)
        {
            ct.ThrowIfCancellationRequested();
            if (!passwordDerivationStarted)
            {
                // A rejected identity still incurs the same password KDF. The result is discarded.
                using var dummyKey = DerivePasswordKey(DummyPasswordSalt);
            }

            // Pad only the remaining time, after the account lifecycle lock has been released.
            var remaining = MinimumCredentialFailureTime - _timeProvider.GetElapsedTime(startedAt);
            if (remaining > TimeSpan.Zero)
            {
                var delay = TimeSpan.FromMilliseconds(Math.Ceiling(remaining.TotalMilliseconds));
                await Task.Delay(delay, _timeProvider, ct);
            }
            ct.ThrowIfCancellationRequested();
            throw new UnauthorizedAccessException("Authentication failed.");
        }

        EncryptionKey DerivePasswordKey(byte[] salt)
        {
            passwordDerivationStarted = true;
            return EncryptionKey.FromPassword(request.Password, salt);
        }
    }

    private async Task<Guid> ResolveAndLoginAsync(
        LoginRequest request, Func<byte[], EncryptionKey> derivePasswordKey, CancellationToken ct)
    {
        var usernameBytes = Encoding.UTF8.GetBytes(request.Username);
        try
        {
            var initialResolution = await _userLookup.ResolveUsernameAsync(usernameBytes, ct);
            if (!CanAttemptLogin(initialResolution) && _recoveryCoordinator is not null &&
                initialResolution.State is (UserLoginIdentityMatchState.InvalidProjection or UserLoginIdentityMatchState.ProjectionOutdated))
                initialResolution = await _loginIdentities.FindSignedRecoveryCandidateAsync(usernameBytes, ct);
            if (!CanAttemptLogin(initialResolution) || initialResolution.UserId is not Guid userId)
                throw new UserNotFoundException();

            return await _lifecycle.ExecuteAsync(
                userId,
                token => LoginResolvedUnderLifecycleAsync(request, usernameBytes, userId, derivePasswordKey, token),
                ct);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(usernameBytes);
        }
    }

    private bool CanAttemptLogin(UserLoginIdentityMatchResult result) =>
        result.UserId.HasValue && (result.State == UserLoginIdentityMatchState.Matched ||
            (_recoveryCoordinator is not null && result.State is
                UserLoginIdentityMatchState.InvalidProjection or UserLoginIdentityMatchState.ProjectionOutdated));

    private async Task<Guid> LoginResolvedUnderLifecycleAsync(
        LoginRequest request,
        byte[] usernameBytes,
        Guid expectedUserId,
        Func<byte[], EncryptionKey> derivePasswordKey,
        CancellationToken ct)
    {
        if (_preparation is not null)
            await _preparation.ReconcilePendingSecurityOperationsAsync(expectedUserId, ct);
        // A control operation can replace the salt, username projection, or local authorization.
        // Resolve identity and load the canonical user only after those prerequisites complete.
        var prePasswordResolution = await _userLookup.ResolveUsernameAsync(usernameBytes, ct);
        if (prePasswordResolution.State is UserLoginIdentityMatchState.InvalidProjection or UserLoginIdentityMatchState.ProjectionOutdated)
        {
            await _loginIdentities.RecalculateAsync(expectedUserId, ct);
            prePasswordResolution = await _userLookup.ResolveUsernameAsync(usernameBytes, ct);
            if (!CanAttemptLogin(prePasswordResolution) && _recoveryCoordinator is not null)
                prePasswordResolution = await _loginIdentities.FindSignedRecoveryCandidateAsync(usernameBytes, ct);
        }
        if (!CanAttemptLogin(prePasswordResolution) || prePasswordResolution.UserId != expectedUserId)
            throw new UsernameChangedDuringLoginException();

        var user = await _userLookup.GetUserByUidAsync(expectedUserId, ct)
                   ?? throw new UserNotFoundException();
        if (await _membershipAuthorizations.GetActiveAsync(
                expectedUserId, _identity.LocalDeviceId, _identity.OriginInstanceId, ct) is null)
            throw new UnauthorizedAccessException("This device is no longer authorized for the account.");
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

            if (prePasswordResolution.State != UserLoginIdentityMatchState.Matched)
            {
                authenticatedRecoverySalt ??= _recoveryCoordinator is null ? null :
                    await _recoveryCoordinator.TryResolvePasswordSaltAsync(user.UId, ct);
                if (authenticatedRecoverySalt is null)
                    throw new UnauthorizedAccessException("Authentication failed.");
            }
            var passwordSalt = authenticatedRecoverySalt ?? user.PasswordSalt;
            if (passwordSalt.Length != CryptographyConstants.Sha256HashSizeInBytes)
                throw new UnauthorizedAccessException("Authentication failed.");

            using var key = derivePasswordKey(passwordSalt);

            using var localBundle = _preparation is not null && _postLogin is not null
                ? await _preparation.TryPrepareLocalAsync(user, key, UserSyncKeyConfidence.UnconfirmedPassword, ct)
                : null;
            if (localBundle is null)
            {
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
            }

            user = await _userLookup.GetAndVerifyUserByUidAsync(user.UId, ct);
            using var bundle = localBundle ?? await _userDataReader.GetAndVerifyUserDataBundleAsync(user, key, ct);
            try
            {
                UserLoginIdentityMetadataUtil.Verify(user, bundle.GeneralUserData);
                if (localBundle is null && _canonicalHealth is not null)
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

                    if (_preparation is not null)
                        await _preparation.EnsureAuthorizedAsync(user.UId, ct);
                    var session = _sessionIssuer.IssueAuthenticatedSession(user.UId, key, bundle);
                    if (localBundle is not null)
                        _postLogin!.DeferUntilInitialRead(user.UId, user.KeyEpoch, user.MembershipEpoch, session);
                    return session;
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
