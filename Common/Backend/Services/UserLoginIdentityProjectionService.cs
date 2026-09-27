using PasswordManagerLocal.Common.Backend.Constants;
using PasswordManagerLocal.Common.Backend.Abstractions.Persistence;
using PasswordManagerLocal.Common.Backend.Abstractions.Repositories;
using PasswordManagerLocal.Common.Backend.Abstractions.Services;
using PasswordManagerLocal.Common.Backend.Models;
using PasswordManagerLocal.Common.Backend.Models.Encrypted;
using PasswordManagerLocal.Common.Backend.Security;
using PasswordManagerLocal.Common.Backend.Sync;
using PasswordManagerLocal.Common.Backend.Utils;
using System.Security.Cryptography;
using System.Text.Json;

using PasswordManagerLocal.Common.Backend.Internal.Identity;
namespace PasswordManagerLocal.Common.Backend.Services;

/// <summary>
/// Rebuildable effective login-identity projection. UserLoginIdentityCandidate ordering uses the same
/// deterministic GeneralUserData version used by the encrypted true-merge path.
/// </summary>
public sealed class UserLoginIdentityProjectionService : IUserLoginIdentityProjectionService
{
    private readonly IUserRepository _users;
    private readonly IUserSyncSnapshotRepository _snapshots;
    private readonly IUserMembershipAuthorizationService _membershipAuthorization;
    private readonly IUserLifecycleCoordinator _lifecycle;
    private readonly IUnitOfWork _uow;
    private readonly IDeletedUserBarrierRepository? _deletionBarriers;
    private readonly IUserCanonicalHealthService? _canonicalHealth;

    public UserLoginIdentityProjectionService(
        IUserRepository users,
        IUserSyncSnapshotRepository snapshots,
        IUserMembershipAuthorizationService membershipAuthorization,
        IUserLifecycleCoordinator lifecycle,
        IUnitOfWork uow,
        IDeletedUserBarrierRepository? deletionBarriers = null,
        IUserCanonicalHealthService? canonicalHealth = null)
    {
        _users = users;
        _snapshots = snapshots;
        _membershipAuthorization = membershipAuthorization;
        _lifecycle = lifecycle;
        _uow = uow;
        _deletionBarriers = deletionBarriers;
        _canonicalHealth = canonicalHealth;
    }

    public async Task<UserLoginIdentityState> SetCanonicalAsync(
        User user,
        SyncVersionStamp generalUserDataVersion,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(user);
        if (_deletionBarriers is not null && await _deletionBarriers.ExistsAsync(user.UId, ct))
            throw new InvalidOperationException("A permanently deleted account cannot recreate a login-identity projection.");
        SyncVersionStampComparer.Validate(generalUserDataVersion);
        ValidateUsernameMetadata(user.UsernameHash, user.UsernameSalt);

        var existing = await _users.GetLoginIdentityStateAsync(user.UId, ct);
        if (existing is not null && existing.KeyEpoch == user.KeyEpoch)
        {
            var comparison = SyncVersionStampComparer.Instance.Compare(existing.Version, generalUserDataVersion);
            if (comparison > 0)
            {
                throw new InvalidOperationException(
                    "Canonical general-user-data cannot move behind the currently effective deterministic username version.");
            }
            if (comparison == 0 && !SameUsernameMetadata(existing.UsernameHash, existing.UsernameSalt, user.UsernameHash, user.UsernameSalt))
            {
                throw new InvalidDataException(
                    "Equal deterministic general-data versions advertise different username metadata.");
            }
        }

        user.SetGeneralUserDataVersion(generalUserDataVersion);
        var candidate = UserLoginIdentityCandidate.FromCanonical(user, generalUserDataVersion);
        return await UpsertAsync(existing, candidate, UserLoginIdentityStatus.Active, null, ct);
    }

    public Task<UserLoginIdentityState?> RecalculateAsync(Guid userId, CancellationToken ct = default) =>
        _lifecycle.ExecuteAsync(
            userId,
            async token =>
            {
                var result = await RecalculateCoreAsync(userId, token);
                await _uow.SaveChangesAsync(token);
                return result;
            },
            ct);

    public Task<UserLoginIdentityState?> RecalculateUnderLifecycleAsync(Guid userId, CancellationToken ct = default) =>
        RecalculateCoreAsync(userId, ct);

    public async Task<UserLoginIdentityMatchResult> FindByUsernameAsync(
        byte[] normalizedUsername,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(normalizedUsername);
        var matches = new List<UserLoginIdentityState>();
        var identities = await _users.ListLoginIdentityStatesAsync(ct);
        var projectedUserIds = identities.Select(identity => identity.UserId).ToHashSet();
        var missingProjectionUserIds = (await _users.ListUserIdsAsync(ct))
            .Where(userId => !projectedUserIds.Contains(userId))
            .Distinct()
            .ToArray();
        if (missingProjectionUserIds.Length != 0)
        {
            foreach (var userId in missingProjectionUserIds)
                await RecalculateAsync(userId, ct);
            identities = await _users.ListLoginIdentityStatesAsync(ct);
        }

        var hasUnusableProjection = false;
        foreach (var identity in identities)
        {
            if (identity.UsernameHash.Length != CryptographyConstants.Sha256HashSizeInBytes ||
                identity.UsernameSalt.Length != CryptographyConstants.Sha256HashSizeInBytes)
            {
                hasUnusableProjection = true;
                continue;
            }
            if (identity.Status != UserLoginIdentityStatus.Active)
                hasUnusableProjection = true;

            var calculatedHash = Hashing.SHA256Hash(normalizedUsername, identity.UsernameSalt);
            try
            {
                if (Hashing.Verify(identity.UsernameHash, calculatedHash))
                    matches.Add(identity);
            }
            finally
            {
                CryptographicOperations.ZeroMemory(calculatedHash);
            }
        }

        if (matches.Count == 0)
        {
            return hasUnusableProjection
                ? new(UserLoginIdentityMatchState.InvalidProjection, Diagnostic: "At least one active account has an unusable login-identity projection.")
                : new(UserLoginIdentityMatchState.NotFound);
        }
        if (matches.Count > 1)
            return new(UserLoginIdentityMatchState.Ambiguous, Diagnostic: "Multiple effective identities match the supplied normalized username.");

        var match = matches[0];
        if (match.Status == UserLoginIdentityStatus.IntegrityConflict)
            return new(UserLoginIdentityMatchState.ProjectionQuarantined, Diagnostic: match.StatusReason);
        if (match.Status != UserLoginIdentityStatus.Active)
            return new(UserLoginIdentityMatchState.InvalidProjection, Diagnostic: match.StatusReason);

        if (_deletionBarriers is not null && await _deletionBarriers.ExistsAsync(match.UserId, ct))
            return new(UserLoginIdentityMatchState.DeletedAccount);

        var user = await _users.GetByIdAsync(match.UserId, ct);
        if (user is null)
            return new(UserLoginIdentityMatchState.InvalidProjection, Diagnostic: "The projected user no longer exists.");
        if (match.KeyEpoch != user.KeyEpoch || match.MembershipEpoch > user.MembershipEpoch)
            return new(UserLoginIdentityMatchState.ProjectionOutdated, Diagnostic: "The projection epochs do not match canonical lifecycle state.");

        try
        {
            await VerifyProjectionSourceAsync(match, user, ct);
        }
        catch (Exception ex) when (ex is InvalidDataException or UnauthorizedAccessException)
        {
            return new(UserLoginIdentityMatchState.InvalidProjection, Diagnostic: ex.Message);
        }

        return new(UserLoginIdentityMatchState.Matched, match.UserId, match);
    }

    private async Task<UserLoginIdentityState?> RecalculateCoreAsync(Guid userId, CancellationToken ct)
    {
        var existing = await _users.GetLoginIdentityStateAsync(userId, ct);
        if (_deletionBarriers is not null && await _deletionBarriers.ExistsAsync(userId, ct))
        {
            if (existing is not null)
                _users.DeleteLoginIdentityState(existing);
            return null;
        }

        var user = await _users.GetByIdAsync(userId, ct);
        if (user is null)
        {
            if (existing is not null)
                _users.DeleteLoginIdentityState(existing);
            return null;
        }

        if (_canonicalHealth is not null)
        {
            var canonicalHealth = await _canonicalHealth.VerifyAsync(
                user,
                key: null,
                keyConfidence: UserSyncKeyConfidence.UnconfirmedPassword,
                recordFault: true,
                ct: ct);
            if (!canonicalHealth.IsPublishable)
            {
                return await UpsertInvalidAsync(
                    existing,
                    user,
                    UserLoginIdentityStatus.InvalidSource,
                    "The local canonical login identity is not covered by a valid signed checkpoint.",
                    ct);
            }
        }

        UserLoginIdentityCandidate winner;
        try
        {
            var canonicalVersion = user.GetGeneralUserDataVersion();
            SyncVersionStampComparer.Validate(canonicalVersion);
            ValidateUsernameMetadata(user.UsernameHash, user.UsernameSalt);
            winner = UserLoginIdentityCandidate.FromCanonical(user, canonicalVersion);
        }
        catch (InvalidDataException ex)
        {
            return await UpsertInvalidAsync(existing, user, UserLoginIdentityStatus.InvalidSource, ex.Message, ct);
        }

        foreach (var row in await _snapshots.ListForUserAsync(userId, ct))
        {
            if (row.UserKeyEpoch != user.KeyEpoch || row.MembershipEpoch > user.MembershipEpoch)
                continue;
            if (row.Status == UserSyncSnapshotStatus.IsolatedFork)
            {
                return await UpsertInvalidAsync(
                    existing, user, UserLoginIdentityStatus.IntegrityConflict,
                    "A terminal origin fork prevents proving an effective login identity.", ct);
            }
            if (row.Status is UserSyncSnapshotStatus.IsolatedCorrupt or UserSyncSnapshotStatus.RecoveryCandidate)
            {
                try
                {
                    var isolatedEnvelope = DeserializeAndValidate(row);
                    await _membershipAuthorization.VerifySnapshotAuthorAsync(isolatedEnvelope, ct);
                    if (SyncVersionStampComparer.Instance.Compare(
                            isolatedEnvelope.User.GeneralUserDataVersion, winner.Version) > 0)
                    {
                        return await UpsertInvalidAsync(
                            existing, user, UserLoginIdentityStatus.InvalidSource,
                            "A newer signed username mutation exists only in isolated, not yet verified evidence.", ct);
                    }
                }
                catch (Exception ex) when (ex is InvalidDataException or UnauthorizedAccessException or JsonException)
                {
                    return await UpsertInvalidAsync(existing, user, UserLoginIdentityStatus.InvalidSource, ex.Message, ct);
                }
                continue;
            }
            if (row.Status == UserSyncSnapshotStatus.SupersededBadEvidence)
                continue;

            UserSnapshotEnvelope envelope;
            try
            {
                envelope = DeserializeAndValidate(row);
                await _membershipAuthorization.VerifySnapshotAuthorAsync(envelope, ct);
                SyncVersionStampComparer.Validate(envelope.User.GeneralUserDataVersion);
                ValidateUsernameMetadata(envelope.User.UsernameHash, envelope.User.UsernameSalt);
            }
            catch (Exception ex) when (ex is InvalidDataException or UnauthorizedAccessException or JsonException)
            {
                return await UpsertInvalidAsync(existing, user, UserLoginIdentityStatus.InvalidSource, ex.Message, ct);
            }

            var candidate = UserLoginIdentityCandidate.FromSnapshot(envelope);
            var comparison = SyncVersionStampComparer.Instance.Compare(candidate.Version, winner.Version);
            if (comparison == 0)
            {
                if (!SameUsernameMetadata(candidate.UsernameHash, candidate.UsernameSalt, winner.UsernameHash, winner.UsernameSalt))
                {
                    return await UpsertInvalidAsync(
                        existing,
                        user,
                        UserLoginIdentityStatus.IntegrityConflict,
                        "Equal deterministic general-data versions advertise different username metadata.",
                        ct);
                }
                continue;
            }

            if (row.Status == UserSyncSnapshotStatus.MergedReceipt && comparison > 0)
            {
                return await UpsertInvalidAsync(
                    existing,
                    user,
                    UserLoginIdentityStatus.InvalidSource,
                    "Merged receipt evidence contains a username version newer than canonical state.",
                    ct);
            }

            if (comparison > 0)
                winner = candidate;
        }

        return await UpsertAsync(existing, winner, UserLoginIdentityStatus.Active, null, ct);
    }

    private async Task VerifyProjectionSourceAsync(UserLoginIdentityState projection, User user, CancellationToken ct)
    {
        SyncVersionStampComparer.Validate(projection.Version);
        if (projection.SourceOriginRevision == 0)
        {
            if (projection.KeyEpoch != user.KeyEpoch || projection.MembershipEpoch != user.MembershipEpoch ||
                !SyncVersionStampComparer.Instance.Equals(projection.Version, user.GetGeneralUserDataVersion()) ||
                !SameUsernameMetadata(projection.UsernameHash, projection.UsernameSalt, user.UsernameHash, user.UsernameSalt))
            {
                throw new InvalidDataException("The canonical login projection no longer matches canonical metadata.");
            }
            return;
        }

        if (projection.SourceOriginDeviceId == Guid.Empty ||
            projection.SourceOriginInstanceId == Guid.Empty ||
            projection.SourceOriginRevision <= 0 ||
            projection.SourceSnapshotHash.Length != Constants.SyncConstants.SyncDeltaPayloadHashBytes)
        {
            throw new InvalidDataException("The effective login projection has invalid immutable source identity metadata.");
        }

        var row = await _snapshots.GetExactAsync(
            projection.UserId,
            projection.SourceOriginDeviceId,
            projection.SourceOriginInstanceId,
            projection.KeyEpoch,
            projection.SourceOriginRevision,
            ct);
        if (row is null || row.Status is not (UserSyncSnapshotStatus.Pending or UserSyncSnapshotStatus.LocalPublished or UserSyncSnapshotStatus.MergedReceipt))
            throw new InvalidDataException("The effective login projection source is no longer retained or eligible.");

        var envelope = DeserializeAndValidate(row);
        await _membershipAuthorization.VerifySnapshotAuthorAsync(envelope, ct);
        if (projection.MembershipEpoch != envelope.MembershipEpoch ||
            !Hashing.Verify(projection.SourceSnapshotHash, envelope.SnapshotHash) ||
            !SyncVersionStampComparer.Instance.Equals(projection.Version, envelope.User.GeneralUserDataVersion) ||
            !SameUsernameMetadata(projection.UsernameHash, projection.UsernameSalt, envelope.User.UsernameHash, envelope.User.UsernameSalt))
            throw new InvalidDataException("The effective login projection conflicts with its immutable signed source.");
    }

    private async Task<UserLoginIdentityState> UpsertInvalidAsync(
        UserLoginIdentityState? existing,
        User user,
        UserLoginIdentityStatus status,
        string reason,
        CancellationToken ct)
    {
        var version = user.GetGeneralUserDataVersion();
        if (!version.IsValid)
        {
            version = new SyncVersionStamp
            {
                PhysicalTimeUnixMilliseconds = 1,
                LogicalCounter = 0,
                OriginDeviceId = user.UId,
                OriginInstanceId = user.UId
            };
        }
        var candidate = UserLoginIdentityCandidate.FromCanonical(user, version);
        return await UpsertAsync(existing, candidate, status, reason, ct);
    }

    private async Task<UserLoginIdentityState> UpsertAsync(
        UserLoginIdentityState? existing,
        UserLoginIdentityCandidate candidate,
        UserLoginIdentityStatus status,
        string? reason,
        CancellationToken ct)
    {
        var state = existing ?? new UserLoginIdentityState { UserId = candidate.UserId };
        state.UsernameHash = candidate.UsernameHash.ToArray();
        state.UsernameSalt = candidate.UsernameSalt.ToArray();
        state.Version = candidate.Version;
        state.SourceOriginDeviceId = candidate.SourceOriginDeviceId;
        state.SourceOriginInstanceId = candidate.SourceOriginInstanceId;
        state.SourceOriginRevision = candidate.SourceOriginRevision;
        state.SourceSnapshotHash = candidate.SourceSnapshotHash.ToArray();
        state.KeyEpoch = candidate.KeyEpoch;
        state.MembershipEpoch = candidate.MembershipEpoch;
        state.Status = status;
        state.StatusReason = reason is null ? null : reason[..Math.Min(reason.Length, 512)];
        state.UpdatedAtUtc = DateTimeOffset.UtcNow;
        state.ConcurrencyVersion = checked(state.ConcurrencyVersion + 1);

        if (existing is null)
            await _users.AddLoginIdentityStateAsync(state, ct);
        else
            _users.UpdateLoginIdentityState(state);
        return state;
    }

    private UserSnapshotEnvelope DeserializeAndValidate(UserSyncSnapshot row)
    {
        if (row.EnvelopePayload.Length == 0 || row.EnvelopePayload.Length > Constants.SyncConstants.MaxUserSnapshotEnvelopeBytes)
            throw new InvalidDataException("The stored user snapshot envelope size is invalid.");
        var envelope = JsonSerializer.Deserialize(row.EnvelopePayload, BackendJsonSerializerContext.Default.UserSnapshotEnvelope)
            ?? throw new InvalidDataException("The stored user snapshot envelope is invalid.");
        UserSnapshotEnvelopeUtil.ValidateStructureAndHash(envelope);
        if (row.UserId != envelope.UserId ||
            row.OriginDeviceId != envelope.OriginDeviceId ||
            row.OriginInstanceId != envelope.OriginInstanceId ||
            row.OriginRevision != envelope.OriginRevision ||
            row.UserKeyEpoch != envelope.UserKeyEpoch ||
            row.MembershipEpoch != envelope.MembershipEpoch ||
            !Hashing.Verify(row.SnapshotHash, envelope.SnapshotHash))
            throw new InvalidDataException("The stored snapshot row conflicts with its immutable signed envelope.");
        return envelope;
    }

    private void ValidateUsernameMetadata(byte[] hash, byte[] salt)
    {
        if (hash.Length != CryptographyConstants.Sha256HashSizeInBytes || salt.Length != CryptographyConstants.Sha256HashSizeInBytes)
            throw new InvalidDataException("The authenticated username projection metadata has an invalid size.");
    }

    private bool SameUsernameMetadata(byte[] firstHash, byte[] firstSalt, byte[] secondHash, byte[] secondSalt) =>
        Hashing.Verify(firstHash, secondHash) && Hashing.Verify(firstSalt, secondSalt);

}
