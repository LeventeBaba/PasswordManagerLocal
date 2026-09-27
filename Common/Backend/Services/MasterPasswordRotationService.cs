using PasswordManagerLocal.Common.Backend.Abstractions.Persistence;
using PasswordManagerLocal.Common.Backend.Abstractions.Repositories;
using PasswordManagerLocal.Common.Backend.Abstractions.Services;
using PasswordManagerLocal.Common.Backend.Exceptions;
using PasswordManagerLocal.Common.Backend.Internal.Authentication;
using PasswordManagerLocal.Common.Backend.Models;
using PasswordManagerLocal.Common.Contracts.Requests;
using PasswordManagerLocal.Common.Backend.Security;
using PasswordManagerLocal.Common.Backend.Sync;
using System.Security.Cryptography;

namespace PasswordManagerLocal.Common.Backend.Services;

public sealed class MasterPasswordRotationService : IMasterPasswordRotationService
{
    private readonly IUserLookupService _userLookup;
    private readonly IUserDataReaderService _userDataReader;
    private readonly IUserDataWriterService _userDataWriter;
    private readonly IUserSessionService _userSessions;
    private readonly ITokenService _tokens;
    private readonly IDataCachingService _cache;
    private readonly IKeyVaultService _keys;
    private readonly IRememberMeService _rememberMe;
    private readonly IUnitOfWork _uow;
    private readonly IUserSnapshotMergeCoordinator _snapshotMerge;
    private readonly IUserRepository _users;
    private readonly IUserSyncSnapshotRepository _snapshots;
    private readonly IUserLifecycleCoordinator _lifecycle;
    private readonly IUserControlOperationWriterService _controlWriter;
    private readonly IUserSnapshotPublisherService _snapshotPublisher;
    private readonly ISyncQueueWriterService _queueWriter;
    private readonly IPendingSyncActivationService _activation;
    private readonly ICredentialVerificationService _credentialVerification;

    public MasterPasswordRotationService(
        IUserLookupService userLookup,
        IUserDataReaderService userDataReader,
        IUserDataWriterService userDataWriter,
        IUserSessionService userSessions,
        ITokenService tokens,
        IDataCachingService cache,
        IKeyVaultService keys,
        IRememberMeService rememberMe,
        IUnitOfWork uow,
        IUserSnapshotMergeCoordinator snapshotMerge,
        IUserRepository users,
        IUserSyncSnapshotRepository snapshots,
        IUserLifecycleCoordinator lifecycle,
        IUserControlOperationWriterService controlWriter,
        IUserSnapshotPublisherService snapshotPublisher,
        ISyncQueueWriterService queueWriter,
        IPendingSyncActivationService activation,
        ICredentialVerificationService credentialVerification)
    {
        _userLookup = userLookup;
        _userDataReader = userDataReader;
        _userDataWriter = userDataWriter;
        _userSessions = userSessions;
        _tokens = tokens;
        _cache = cache;
        _keys = keys;
        _rememberMe = rememberMe;
        _uow = uow;
        _snapshotMerge = snapshotMerge;
        _users = users;
        _snapshots = snapshots;
        _lifecycle = lifecycle;
        _controlWriter = controlWriter;
        _snapshotPublisher = snapshotPublisher;
        _queueWriter = queueWriter;
        _activation = activation;
        _credentialVerification = credentialVerification;
    }

    public async Task ChangeMasterPasswordAsync(MasterPasswordChangeRequest request, CancellationToken ct = default)
    {
        if (!request.Validate(out var errors))
            throw new InvalidInputException(errors);

        var initialUser = await _userLookup.GetAndVerifyUserAsync(request.Token, ct);
        await _lifecycle.ExecuteAsync(
            initialUser.UId,
            token => ChangeMasterPasswordCoreAsync(request, initialUser.UId, token),
            ct);
    }

    private async Task ChangeMasterPasswordCoreAsync(
        MasterPasswordChangeRequest request,
        Guid userId,
        CancellationToken ct)
    {
        var currentUser = await _userLookup.GetAndVerifyUserAsync(request.Token, ct);
        if (currentUser.UId != userId || !_credentialVerification.IsPasswordValid(request.Token, request.Password, currentUser.PasswordSalt))
            throw new InvalidInputException();

        using (var currentKey = _userSessions.GetEncryptionKeyFromToken(request.Token))
            await _snapshotMerge.TryMergePendingAsync(userId, currentKey, UserSyncKeyConfidence.AuthenticatedSession, ct);

        var user = await _userLookup.GetAndVerifyUserByUidAsync(userId, ct);
        if (await _snapshots.HasQuarantinedAsync(user.UId, user.KeyEpoch, user.MembershipEpoch, ct))
        {
            throw new InvalidOperationException(
                "The master password cannot be changed while a current-epoch snapshot origin is quarantined or unresolved.");
        }

        // Tombstone causal references target the next snapshot in the epoch where the deletion
        // was authored. Publish the current canonical bytes before rotating the key so no local
        // deletion anchor can be orphaned by the epoch transition. The replacement snapshot then
        // carries this old-epoch merged knowledge forward in its authenticated coverage vector.
        await _snapshotPublisher.GetOrCreateAsync(user, ct);

        _cache.InvalidateToken(request.Token);
        var bundle = await _userDataReader.GetLoadAndVerifyUserDataBundleAsync(request.Token, ct, user);
        var canonicalBackup = CanonicalUserState.Capture(user);
        var previousKeyEpoch = user.KeyEpoch;
        var rememberMeWasEnabled = user.SavedKey is not null;
        if (user.SavedKey is not null)
            CryptographicOperations.ZeroMemory(user.SavedKey);
        user.SavedKey = null;

        CryptographicOperations.ZeroMemory(user.PasswordSalt);
        user.PasswordSalt = Hashing.GenerateSalt();
        user.KeyEpoch = checked(previousKeyEpoch + 1);
        using var newKey = EncryptionKey.FromPassword(request.NewPassword, user.PasswordSalt);

        await using var transaction = await _uow.BeginTransactionAsync(ct);
        try
        {
            await _userDataWriter.ReencryptUserDataBundleWithNewKeysAsync(
                bundle,
                user,
                newKey,
                enqueueSync: false,
                ct);

            await _controlWriter.CreateAppliedKeyEpochReplacementAsync(user, previousKeyEpoch, ct);
            var publishingUser = await _users.GetByIdWithRelationsAsync(user.UId, ct)
                ?? throw new InvalidOperationException("The canonical user disappeared during password rotation.");
            await _snapshotPublisher.GetOrCreateAsync(publishingUser, ct);
            await _queueWriter.EnqueueAsync(
                new SyncItem
                {
                    ModelId = user.UId,
                    ModelType = SyncModelType.User,
                    ChangeType = SyncChangeType.Updated
                },
                DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                [],
                touchLocalSyncState: true,
                activateTargets: false,
                ct);

            await _uow.SaveChangesAsync(ct);
            await transaction.CommitAsync(ct);
        }
        catch
        {
            await transaction.RollbackAsync(CancellationToken.None);
            canonicalBackup.Restore(user);
            _uow.ClearTrackedChanges();
            canonicalBackup.ZeroCopies();
            throw;
        }

        Exception? postCommitFailure = null;
        try
        {
            // In-memory key/session state cannot move ahead of the committed canonical transition.
            if (!_keys.RotateUserKey(request.Token, newKey))
                throw new InvalidOperationException("The initiating session is no longer active.");
            _keys.SetUserBlobKeys(request.Token, bundle.UserData);
            _cache.SetUserDataBundle(request.Token, bundle);

            if (rememberMeWasEnabled)
            {
                _rememberMe.SetRememberMe(user, true, newKey);
                await _userDataWriter.UpdateSavedKeyOnlyAsync(user, ct);
            }
        }
        catch (Exception ex)
        {
            // Canonical state is already committed. Fail closed instead of leaving the initiating
            // token associated with an old or partially refreshed in-memory key state. A failed
            // local SavedKey write must also not remain pending in the scoped EF change tracker.
            if (user.SavedKey is not null)
                CryptographicOperations.ZeroMemory(user.SavedKey);
            user.SavedKey = null;
            _uow.ClearTrackedChanges();
            InvalidateToken(request.Token, AuthSessionInvalidationReason.ProfilePasswordChanged);
            postCommitFailure = ex;
        }
        finally
        {
            foreach (var otherToken in _tokens.ListTokensByUid(user.UId))
            {
                if (otherToken != request.Token)
                    InvalidateToken(otherToken, AuthSessionInvalidationReason.ProfilePasswordChanged);
            }

            try
            {
                // Durable queue/control rows were committed above. Activation is only a wake-up
                // optimization; a failure here must not turn a committed password change into a
                // rollback or remove retryable work.
                await _activation.ActivatePendingAsync(CancellationToken.None);
            }
            catch
            {
            }

            canonicalBackup.ZeroCopies();
        }

        if (postCommitFailure is not null)
        {
            throw new MutationPartiallyCommittedException(
                "The master password was changed durably, but the local authenticated session could not be refreshed.",
                innerException: postCommitFailure);
        }
    }

    private void InvalidateToken(Guid token, AuthSessionInvalidationReason reason)
    {
        _cache.InvalidateToken(token);
        _keys.InvalidateToken(token);
        _tokens.Revoke(token, reason);
    }
}
