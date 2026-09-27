using Microsoft.Extensions.DependencyInjection;
using PasswordManagerLocal.Common.Backend.Abstractions.Persistence;
using PasswordManagerLocal.Common.Backend.Abstractions.Repositories;
using PasswordManagerLocal.Common.Backend.Abstractions.Services;
using PasswordManagerLocal.Common.Backend.Constants;
using PasswordManagerLocal.Common.Backend.Models;
using PasswordManagerLocal.Common.Backend.Security;
using PasswordManagerLocal.Common.Backend.Sync;
using PasswordManagerLocal.Common.Backend.Utils;
using System.Text.Json;

using PasswordManagerLocal.Common.Backend.Internal.Enrollment;

using PasswordManagerLocal.Common.Backend.Sync.Enrollment;
namespace PasswordManagerLocal.Common.Backend.Services;

/// <summary>
/// Validates an authoritative enrollment graph before persistence, preserves the target installation's
/// local identity, and imports immutable control/snapshot evidence transactionally.
/// </summary>
public sealed class DeviceEnrollmentSnapshotImporterService : IDeviceEnrollmentSnapshotImporterService
{
    private readonly IDeviceIdentityService _identity;
    private readonly IDeviceEnrollmentLocalLinkService _localLinks;

    public DeviceEnrollmentSnapshotImporterService(
        IDeviceIdentityService identity,
        IDeviceEnrollmentLocalLinkService localLinks)
    {
        _identity = identity;
        _localLinks = localLinks;
    }

    public async Task ImportAsync(IServiceProvider services, DeviceEnrollmentSnapshot snapshot, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        if (snapshot.PayloadVersion != Constants.SyncConstants.DeviceEnrollmentPayloadVersion)
            throw new InvalidDataException("The enrollment payload version is invalid.");
        var deletionBarriers = services.GetRequiredService<IDeletedUserBarrierRepository>();
        if (snapshot.PrimaryUserId != Guid.Empty && await deletionBarriers.ExistsAsync(snapshot.PrimaryUserId, ct))
            throw new InvalidDataException("Enrollment cannot import a permanently deleted account identity.");

        var validated = ValidateCompleteGraph(snapshot);

        var devices = services.GetRequiredService<IDeviceRepository>();
        var users = services.GetRequiredService<IUserRepository>();
        var groups = services.GetRequiredService<IGroupRepository>();
        var userDevices = services.GetRequiredService<IUserDeviceRepository>();
        var localUserDevices = services.GetRequiredService<ILocalUserDeviceRepository>();
        var authorizations = services.GetRequiredService<IUserMembershipAuthorizationRepository>();
        var cutoffs = services.GetRequiredService<IUserOriginRemovalCutoffRepository>();
        var operations = services.GetRequiredService<IUserControlOperationRepository>();
        var controlStates = services.GetRequiredService<IUserControlStateRepository>();
        var revisionKnowledge = services.GetRequiredService<IUserRevisionKnowledgeRepository>();
        var pendingSnapshots = services.GetRequiredService<IUserSyncSnapshotRepository>();
        var syncStates = services.GetRequiredService<IUserSyncStateRepository>();
        var unitOfWork = services.GetRequiredService<IUnitOfWork>();
        var syncIdentities = services.GetRequiredService<ISyncDeviceIdentityService>();
        var loginIdentities = services.GetRequiredService<IUserLoginIdentityProjectionService>();
        var canonicalHealth = services.GetRequiredService<IUserCanonicalHealthService>();
        var versionClock = services.GetRequiredService<ISyncVersionClockService>();

        var existingState = await controlStates.GetAsync(snapshot.PrimaryUserId, ct);
        if (existingState is not null)
        {
            if (existingState.AppliedKeyEpoch > validated.User.KeyEpoch ||
                existingState.AppliedMembershipEpoch > validated.User.MembershipEpoch)
                throw new InvalidDataException("The enrollment bootstrap is behind the locally retained control barrier.");
            if (existingState.HasConflict && !validated.ControlState.HasConflict)
                throw new InvalidDataException("The enrollment bootstrap cannot clear a locally retained control conflict.");
            if (existingState.HasConflict && validated.ControlState.HasConflict &&
                (!Nullable.Equals(existingState.ConflictingOperationId, validated.ControlState.ConflictingOperationId) ||
                 !HashesEqual(existingState.ConflictingOperationHash, validated.ControlState.ConflictingOperationHash)))
                throw new InvalidDataException("The enrollment bootstrap conflicts with the locally retained control quarantine.");
            if (existingState.LocalOriginInstanceId != Guid.Empty && existingState.LocalOriginInstanceId != _identity.OriginInstanceId)
                throw new InvalidDataException("The local control state belongs to a different installation origin.");
        }

        await using var transaction = await unitOfWork.BeginTransactionAsync(ct);
        try
        {
            if (await deletionBarriers.ExistsAsync(snapshot.PrimaryUserId, ct))
                throw new InvalidDataException("Account deletion won the enrollment race before bootstrap persistence.");

            await _localLinks.RemoveLocalDeviceRowsAsync(devices, ct);

            foreach (var deviceSnapshot in validated.CurrentRemoteDevices)
                await UpsertRemoteDeviceAsync(devices, userDevices, deviceSnapshot, ct);

            var user = await users.GetByIdAsync(validated.User.UId, ct);
            if (user is null)
            {
                user = new User { UId = validated.User.UId };
                await users.AddAsync(user, ct);
            }
            ApplyUser(validated.User, user);

            foreach (var groupSnapshot in snapshot.Groups)
            {
                var group = await groups.GetByIdWithUsersAsync(groupSnapshot.Id, ct);
                if (group is null)
                {
                    group = new Group { Id = groupSnapshot.Id };
                    await groups.AddAsync(group, ct);
                }
                group.EncryptedPayload = groupSnapshot.EncryptedPayload.ToArray();
                group.LastModifiedAt = UtcDateTimeUtil.ToUtc(groupSnapshot.LastModifiedAt);
                group.IntegrityHash = groupSnapshot.IntegrityHash.ToArray();
            }
            await unitOfWork.SaveChangesAsync(ct);

            foreach (var groupSnapshot in snapshot.Groups)
            {
                var group = await groups.GetByIdWithUsersAsync(groupSnapshot.Id, ct)
                    ?? throw new InvalidDataException("An enrollment group could not be persisted.");
                var importedUserIds = groupSnapshot.UserIds.Where(id => id != Guid.Empty).Distinct().ToHashSet();
                foreach (var current in group.Users.Where(item => !importedUserIds.Contains(item.UId)).ToList())
                    group.Users.Remove(current);
                foreach (var importedUserId in importedUserIds)
                {
                    if (group.Users.Any(item => item.UId == importedUserId))
                        continue;
                    var importedUser = await users.GetByIdAsync(importedUserId, ct);
                    if (importedUser is not null)
                        group.Users.Add(importedUser);
                }
            }

            foreach (var row in snapshot.MembershipAuthorizations)
            {
                var existing = await authorizations.GetByIdAsync(row.AuthorizationId, ct);
                if (existing is null)
                {
                    await authorizations.AddAsync(ToAuthorization(row), ct);
                }
                else
                {
                    RequireSameAuthorization(existing, row);
                }
            }

            foreach (var row in snapshot.RemovalCutoffs)
            {
                var existing = await cutoffs.GetAsync(row.UserId, row.DeviceId, row.OriginInstanceId, row.UserKeyEpoch, ct);
                if (existing is null)
                    await cutoffs.AddAsync(ToCutoff(row), ct);
                else
                    RequireSameCutoff(existing, row);
            }

            foreach (var item in validated.ControlOperations)
            {
                var existing = await operations.GetByIdAsync(item.Envelope.OperationId, ct);
                if (existing is null)
                {
                    var row = UserControlOperationMapping.ToStoredOperation(
                        item.Envelope,
                        item.Snapshot.EnvelopePayload.ToArray(),
                        item.Snapshot.Status,
                        UtcDateTimeUtil.ToUtc(item.Snapshot.ReceivedAtUtc),
                        UtcDateTimeUtil.ToUtc(item.Snapshot.AppliedAtUtc));
                    row.StatusReason = item.Snapshot.StatusReason;
                    row.ConflictingOperationHash = item.Snapshot.ConflictingOperationHash?.ToArray();
                    await operations.AddAsync(row, ct);
                }
                else if (!Hashing.Verify(existing.OperationHash, item.Envelope.OperationHash) ||
                         !existing.EnvelopePayload.SequenceEqual(item.Snapshot.EnvelopePayload))
                {
                    throw new InvalidDataException("A locally retained control operation conflicts with the enrollment bootstrap.");
                }
            }

            var state = await controlStates.GetAsync(snapshot.PrimaryUserId, ct);
            if (state is null)
            {
                state = new UserControlState { UserId = snapshot.PrimaryUserId };
                await controlStates.AddAsync(state, ct);
            }
            state.LocalOriginInstanceId = _identity.OriginInstanceId;
            state.NextOriginSequence = Math.Max(1, state.NextOriginSequence);
            state.AppliedKeyEpoch = validated.ControlState.AppliedKeyEpoch;
            state.AppliedMembershipEpoch = validated.ControlState.AppliedMembershipEpoch;
            state.HasConflict = validated.ControlState.HasConflict;
            state.ConflictReason = validated.ControlState.ConflictReason;
            state.ConflictingOperationId = validated.ControlState.ConflictingOperationId;
            state.ConflictingOperationHash = validated.ControlState.ConflictingOperationHash?.ToArray();
            state.LastUpdatedAtUtc = DateTimeOffset.UtcNow;

            foreach (var row in snapshot.RevisionKnowledge)
            {
                var existing = await revisionKnowledge.GetAsync(row.UserId, row.OriginDeviceId, row.OriginInstanceId, row.UserKeyEpoch, ct);
                if (existing is null)
                {
                    await revisionKnowledge.AddAsync(new UserRevisionKnowledge
                    {
                        UserId = row.UserId,
                        OriginDeviceId = row.OriginDeviceId,
                        OriginInstanceId = row.OriginInstanceId,
                        UserKeyEpoch = row.UserKeyEpoch,
                        HighestStoredRevision = row.HighestStoredRevision,
                        HighestStoredSnapshotHash = row.HighestStoredSnapshotHash.ToArray(),
                        HighestMergedRevision = row.HighestMergedRevision,
                        LastUpdatedAtUtc = UtcDateTimeUtil.ToUtc(row.LastUpdatedAtUtc)
                    }, ct);
                }
                else
                {
                    if (existing.HighestStoredRevision > row.HighestStoredRevision || existing.HighestMergedRevision > row.HighestMergedRevision)
                        throw new InvalidDataException("The enrollment bootstrap would roll revision knowledge backward.");
                    if (existing.HighestStoredRevision == row.HighestStoredRevision &&
                        existing.HighestStoredRevision > 0 &&
                        !Hashing.Verify(existing.HighestStoredSnapshotHash, row.HighestStoredSnapshotHash))
                        throw new InvalidDataException("The enrollment bootstrap conflicts with locally retained exact revision knowledge.");
                    existing.HighestStoredRevision = row.HighestStoredRevision;
                    existing.HighestStoredSnapshotHash = row.HighestStoredSnapshotHash.ToArray();
                    existing.HighestMergedRevision = row.HighestMergedRevision;
                    existing.LastUpdatedAtUtc = UtcDateTimeUtil.ToUtc(row.LastUpdatedAtUtc);
                    revisionKnowledge.Update(existing);
                }
            }

            foreach (var item in validated.PendingSnapshots)
            {
                var envelope = item.Envelope;
                var existing = await pendingSnapshots.GetAsync(envelope.UserId, envelope.OriginDeviceId, envelope.OriginInstanceId, envelope.UserKeyEpoch, ct);
                if (existing is null)
                {
                    await pendingSnapshots.AddAsync(CreateSnapshotRow(item, envelope), ct);
                }
                else if (existing.OriginRevision > envelope.OriginRevision)
                {
                    throw new InvalidDataException("The enrollment bootstrap is behind a locally retained user snapshot.");
                }
                else if (existing.OriginRevision == envelope.OriginRevision)
                {
                    if (!Hashing.Verify(existing.SnapshotHash, envelope.SnapshotHash) ||
                        !existing.EnvelopePayload.SequenceEqual(item.Snapshot.EnvelopePayload))
                        throw new InvalidDataException("A locally retained user snapshot conflicts with the enrollment bootstrap.");
                }
                else
                {
                    if (existing.Status == UserSyncSnapshotStatus.Quarantined)
                        throw new InvalidDataException("The enrollment bootstrap cannot overwrite locally retained snapshot quarantine evidence.");
                    CopySnapshotRow(CreateSnapshotRow(item, envelope), existing);
                    pendingSnapshots.Update(existing);
                }
            }

            var activeDeviceIds = snapshot.MembershipAuthorizations
                .Where(row => row.IsActive)
                .Select(row => row.DeviceId)
                .Distinct()
                .ToHashSet();
            var currentLinks = await userDevices.ListByUserAsync(snapshot.PrimaryUserId, ct);
            foreach (var current in currentLinks)
            {
                var shouldBeActive = current.DeviceId != _identity.LocalDeviceId && activeDeviceIds.Contains(current.DeviceId);
                current.IsDeleted = !shouldBeActive;
                current.IsSyncOn = shouldBeActive;
                current.DeletedAt = shouldBeActive ? null : DateTimeOffset.UtcNow;
                current.LastModifiedAt = DateTimeOffset.UtcNow;
                userDevices.Update(current);
            }
            foreach (var deviceId in activeDeviceIds.Where(id => id != _identity.LocalDeviceId))
            {
                if (currentLinks.Any(link => link.DeviceId == deviceId))
                    continue;
                await userDevices.AddAsync(new UserDevice
                {
                    UserId = snapshot.PrimaryUserId,
                    DeviceId = deviceId,
                    IsSyncOn = true,
                    IsDeleted = false,
                    LastModifiedAt = DateTimeOffset.UtcNow
                }, ct);
            }

            await _localLinks.EnsureLocalUserDeviceAsync(devices, localUserDevices, snapshot.PrimaryUserId, ct);

            var syncState = await syncStates.GetAsync(snapshot.PrimaryUserId, ct);
            if (syncState is null)
            {
                syncState = new UserSyncState { UserId = snapshot.PrimaryUserId, NextOriginRevision = 1 };
                await syncStates.AddAsync(syncState, ct);
            }
            else if (syncState.LocalOriginInstanceId != Guid.Empty && syncState.LocalOriginInstanceId != _identity.OriginInstanceId)
            {
                throw new InvalidDataException("The local snapshot state belongs to a different installation origin.");
            }
            syncState.LocalOriginInstanceId = _identity.OriginInstanceId;
            syncState.NextOriginRevision = Math.Max(1, syncState.NextOriginRevision);
            syncState.LastPublishedContentHash = [];
            syncState.LastUpdatedAtUtc = DateTimeOffset.UtcNow;

            // Enrollment has authenticated the complete canonical graph and bound it to this exact
            // installation identity. Create the installation-local signed checkpoint before exposing
            // the username projection. Normal synchronization can begin immediately after import;
            // without this checkpoint, an incoming snapshot/control operation could recalculate and
            // invalidate the projection before the user's first password login.
            await canonicalHealth.UpdateCheckpointAsync(user, ct);
            await loginIdentities.SetCanonicalAsync(user, validated.User.GeneralUserDataVersion, ct);
            await unitOfWork.SaveChangesAsync(ct);
            await transaction.CommitAsync(ct);
        }
        catch
        {
            await transaction.RollbackAsync(CancellationToken.None);
            unitOfWork.ClearTrackedChanges();
            throw;
        }

        versionClock.Observe(
            validated.PendingSnapshots
                .Select(item => item.Envelope.User.GeneralUserDataVersion)
                .Prepend(validated.User.GeneralUserDataVersion));

        foreach (var device in await devices.ListTrustedUnblockedAsync(ct))
            syncIdentities.TryAdd(device);
        try
        {
            await services.GetRequiredService<IPendingSyncActivationService>().ActivatePendingAsync(CancellationToken.None);
        }
        catch
        {
            // The committed queue remains discoverable for normal retry.
        }
    }

    private ValidatedEnrollmentGraph ValidateCompleteGraph(DeviceEnrollmentSnapshot snapshot)
    {
        if (snapshot.PrimaryUserId == Guid.Empty || snapshot.TargetDeviceId != _identity.LocalDeviceId ||
            snapshot.TargetOriginInstanceId != _identity.OriginInstanceId)
            throw new InvalidDataException("The enrollment bootstrap does not target this exact local installation.");
        if (!Hashing.Verify(snapshot.TargetSignPublicKeyHash, Hashing.SHA256Hash(_identity.SignPublicKey)) ||
            !Hashing.Verify(snapshot.TargetAgreementPublicKeyHash, Hashing.SHA256Hash(_identity.AgreementPublicKey)) ||
            !string.Equals(SyncIdentityUtil.NormalizeFingerprint(snapshot.TargetTlsCertFingerprint), SyncIdentityUtil.NormalizeFingerprint(_identity.FingerprintHex), StringComparison.OrdinalIgnoreCase) ||
            snapshot.TargetDeviceType != _identity.DeviceType)
            throw new InvalidDataException("The enrollment bootstrap target cryptographic identity does not match this installation.");

        var userSnapshot = snapshot.Users.SingleOrDefault(row => row.UId == snapshot.PrimaryUserId)
            ?? throw new InvalidDataException("The enrollment bootstrap must contain exactly one primary user.");
        if (snapshot.MembershipAuthorizations.Count > TombstoneConstants.MaxMembershipHistoryRowsPerUser ||
            snapshot.RemovalCutoffs.Count > TombstoneConstants.MaxRemovalCutoffRowsPerUser ||
            snapshot.RevisionKnowledge.Count > TombstoneConstants.MaxRevisionKnowledgeRowsPerUser ||
            snapshot.PendingSnapshots.Count > TombstoneConstants.MaxCausalSnapshotEvidenceRowsPerUser)
        {
            throw new InvalidDataException("The enrollment causal-evidence baseline exceeds safe limits.");
        }
        if (snapshot.Users.Count(row => row.UId == snapshot.PrimaryUserId) != 1 || userSnapshot.KeyEpoch <= 0 || userSnapshot.MembershipEpoch <= 0)
            throw new InvalidDataException("The enrollment bootstrap contains invalid or default epochs.");
        VerifyUserSnapshot(userSnapshot);

        var authById = snapshot.MembershipAuthorizations.ToDictionary(row => row.AuthorizationId);
        if (authById.Count != snapshot.MembershipAuthorizations.Count || authById.Count == 0)
            throw new InvalidDataException("Membership authorization identifiers are missing or duplicated.");
        if (snapshot.MembershipAuthorizations
            .GroupBy(row => (row.UserId, row.DeviceId, row.OriginInstanceId))
            .Any(group => group.Count() != 1))
            throw new InvalidDataException("An installation origin cannot be authorized more than once, including after removal.");
        if (snapshot.MembershipAuthorizations.Where(row => row.IsActive)
            .GroupBy(row => (row.UserId, row.DeviceId))
            .Any(group => group.Count() != 1))
            throw new InvalidDataException("A physical device cannot have multiple active installation origins for one user.");
        foreach (var auth in snapshot.MembershipAuthorizations)
            ValidateAuthorization(auth, snapshot.PrimaryUserId);

        var targetAuthorization = snapshot.MembershipAuthorizations.SingleOrDefault(row => row.IsActive && row.DeviceId == _identity.LocalDeviceId && row.OriginInstanceId == _identity.OriginInstanceId)
            ?? throw new InvalidDataException("The target installation has no active authoritative membership authorization.");
        if (!Hashing.Verify(targetAuthorization.SignPublicKeyHash, Hashing.SHA256Hash(_identity.SignPublicKey)) ||
            !Hashing.Verify(targetAuthorization.AgreementPublicKeyHash, Hashing.SHA256Hash(_identity.AgreementPublicKey)) ||
            !targetAuthorization.SignPublicKey.SequenceEqual(_identity.SignPublicKey) ||
            !string.Equals(SyncIdentityUtil.NormalizeFingerprint(targetAuthorization.TlsCertFingerprint), SyncIdentityUtil.NormalizeFingerprint(_identity.FingerprintHex), StringComparison.OrdinalIgnoreCase) ||
            targetAuthorization.DeviceType != _identity.DeviceType)
            throw new InvalidDataException("The target membership authorization does not bind this installation identity.");

        var cutoffByNamespace = snapshot.RemovalCutoffs.ToDictionary(row => (row.UserId, row.DeviceId, row.OriginInstanceId, row.UserKeyEpoch));
        if (cutoffByNamespace.Count != snapshot.RemovalCutoffs.Count)
            throw new InvalidDataException("The enrollment bootstrap contains duplicate removal cutoffs.");
        foreach (var cutoff in snapshot.RemovalCutoffs)
            ValidateCutoff(cutoff, authById);

        var controlItems = new List<ValidatedControlOperation>();
        foreach (var operationSnapshot in snapshot.ControlOperations)
        {
            var envelope = UserControlOperationEnvelopeUtil.Deserialize(operationSnapshot.EnvelopePayload);
            if (envelope.UserId != snapshot.PrimaryUserId)
                throw new InvalidDataException("A control operation belongs to another user.");
            var authorization = FindAuthorization(snapshot.MembershipAuthorizations, envelope.OriginDeviceId, envelope.OriginInstanceId, envelope.PreviousMembershipEpoch, envelope.PreviousKeyEpoch);
            UserControlOperationEnvelopeUtil.VerifyWithSigningKey(envelope, authorization.SignPublicKey);
            ValidateControlPayload(envelope);
            if (envelope.OperationType == UserControlOperationType.AccountDeletion)
                throw new InvalidDataException("A deleted account identity cannot be imported through enrollment bootstrap.");
            if (!authorization.IsActive)
            {
                var cutoff = snapshot.RemovalCutoffs.FirstOrDefault(row => row.AuthorizationId == authorization.AuthorizationId && row.UserKeyEpoch == envelope.PreviousKeyEpoch)
                    ?? throw new InvalidDataException("A removed control-operation author has no authenticated cutoff.");
                if (envelope.OriginSequence > cutoff.HighestAcceptedControlSequence)
                    throw new InvalidDataException("A retained control operation exceeds its removal cutoff.");
            }
            controlItems.Add(new ValidatedControlOperation(operationSnapshot, envelope));
        }
        if (controlItems.Select(item => item.Envelope.OperationId).Distinct().Count() != controlItems.Count)
            throw new InvalidDataException("The enrollment bootstrap contains duplicate control-operation identifiers.");

        var additionItem = controlItems.SingleOrDefault(item => item.Envelope.OperationId == snapshot.AuthorizingAdditionOperationId)
            ?? throw new InvalidDataException("The exact authorizing device-addition operation is missing.");
        if (additionItem.Envelope.OperationType != UserControlOperationType.DeviceAddition || additionItem.Snapshot.Status != UserControlOperationStatus.Applied ||
            !Hashing.Verify(additionItem.Envelope.OperationHash, snapshot.AuthorizingAdditionOperationHash) ||
            targetAuthorization.AdditionOperationId != additionItem.Envelope.OperationId || targetAuthorization.AdditionOperationHash is null ||
            !Hashing.Verify(targetAuthorization.AdditionOperationHash, additionItem.Envelope.OperationHash))
            throw new InvalidDataException("The target authorization does not match an applied immutable addition operation.");
        var additionPayload = UserControlOperationEnvelopeUtil.DeserializeDeviceAdditionPayload(additionItem.Envelope.OperationPayload);
        if (additionPayload.NewDeviceId != _identity.LocalDeviceId || additionPayload.NewOriginInstanceId != _identity.OriginInstanceId ||
            !additionPayload.SignPublicKey.SequenceEqual(_identity.SignPublicKey) ||
            !additionPayload.AgreementPublicKey.SequenceEqual(_identity.AgreementPublicKey) ||
            !string.Equals(SyncIdentityUtil.NormalizeFingerprint(additionPayload.TlsCertFingerprint), SyncIdentityUtil.NormalizeFingerprint(_identity.FingerprintHex), StringComparison.OrdinalIgnoreCase) ||
            additionPayload.DeviceType != _identity.DeviceType || additionPayload.ResultingMembershipEpoch != userSnapshot.MembershipEpoch)
            throw new InvalidDataException("The authorizing addition payload does not authorize this exact target installation.");

        ValidateAppliedTransitionChains(controlItems, userSnapshot);

        var controlState = snapshot.ControlStates.SingleOrDefault(row => row.UserId == snapshot.PrimaryUserId)
            ?? throw new InvalidDataException("The authoritative control state is missing.");
        if (controlState.AppliedKeyEpoch != userSnapshot.KeyEpoch || controlState.AppliedMembershipEpoch != userSnapshot.MembershipEpoch)
            throw new InvalidDataException("The imported control state does not match canonical epochs.");

        var pendingItems = new List<ValidatedPendingSnapshot>();
        foreach (var pending in snapshot.PendingSnapshots)
        {
            if (!Enum.IsDefined(pending.Status))
                throw new InvalidDataException("A retained user snapshot has an invalid status.");
            if (pending.Status == UserSyncSnapshotStatus.Quarantined)
            {
                if (string.IsNullOrWhiteSpace(pending.QuarantineReason) ||
                    pending.ConflictingSnapshotHash is { Length: not CryptographyConstants.Sha256HashSizeInBytes })
                {
                    throw new InvalidDataException("Quarantined snapshot evidence contains invalid diagnostics.");
                }
            }
            else
            {
                var hasQuarantineReason = !string.IsNullOrWhiteSpace(pending.QuarantineReason);
                var hasConflictingSnapshotHash = pending.ConflictingSnapshotHash is { Length: > 0 };
                if (hasQuarantineReason || hasConflictingSnapshotHash)
                    throw new InvalidDataException("Non-quarantined snapshot evidence contains quarantine metadata.");

                // Treat empty strings and zero-length byte arrays as the same absence value as null.
                // This keeps the imported persistence state canonical without accepting meaningful
                // quarantine diagnostics on a healthy snapshot status.
                pending.QuarantineReason = null;
                pending.ConflictingSnapshotHash = null;
            }

            var envelope = JsonSerializer.Deserialize(pending.EnvelopePayload, BackendJsonSerializerContext.Default.UserSnapshotEnvelope)
                ?? throw new InvalidDataException("A retained user snapshot envelope is invalid.");
            UserSnapshotEnvelopeUtil.ValidateStructureAndHash(envelope);
            if (envelope.UserId != snapshot.PrimaryUserId)
                throw new InvalidDataException("A retained user snapshot belongs to another user.");
            var authorization = FindAuthorization(snapshot.MembershipAuthorizations, envelope.OriginDeviceId, envelope.OriginInstanceId, envelope.MembershipEpoch, envelope.UserKeyEpoch);
            UserSnapshotEnvelopeUtil.VerifyWithSigningKey(envelope, authorization.SignPublicKey);
            if (!authorization.IsActive)
            {
                if (!cutoffByNamespace.TryGetValue((envelope.UserId, envelope.OriginDeviceId, envelope.OriginInstanceId, envelope.UserKeyEpoch), out var cutoff) ||
                    envelope.OriginRevision > cutoff.HighestAcceptedSnapshotRevision)
                    throw new InvalidDataException("A retained user snapshot exceeds its removal cutoff.");
            }
            pendingItems.Add(new ValidatedPendingSnapshot(pending, envelope));
        }

        foreach (var knowledge in snapshot.RevisionKnowledge)
        {
            if (knowledge.UserId != snapshot.PrimaryUserId || knowledge.OriginDeviceId == Guid.Empty || knowledge.OriginInstanceId == Guid.Empty ||
                knowledge.UserKeyEpoch <= 0 || knowledge.HighestStoredRevision < 0 || knowledge.HighestMergedRevision < 0 ||
                (knowledge.HighestStoredRevision > 0 && knowledge.HighestStoredSnapshotHash.Length != CryptographyConstants.Sha256HashSizeInBytes))
                throw new InvalidDataException("The enrollment bootstrap contains invalid revision knowledge.");
        }

        var currentRemoteDevices = new List<DeviceEnrollmentDeviceSnapshot>();
        foreach (var activeGroup in snapshot.MembershipAuthorizations.Where(row => row.IsActive).GroupBy(row => row.DeviceId))
        {
            if (activeGroup.Key == _identity.LocalDeviceId)
                continue;
            var device = snapshot.Devices.SingleOrDefault(row => row.Id == activeGroup.Key)
                ?? throw new InvalidDataException("An active remote membership has no trusted device identity.");
            ValidateDeviceSnapshot(device);
            foreach (var auth in activeGroup)
            {
                if (!device.SignPublicKey.SequenceEqual(auth.SignPublicKey) ||
                    !Hashing.Verify(auth.AgreementPublicKeyHash, Hashing.SHA256Hash(device.PublicKey)) ||
                    !string.Equals(SyncIdentityUtil.NormalizeFingerprint(auth.TlsCertFingerprint), SyncIdentityUtil.NormalizeFingerprint(device.TlsCertFingerprint), StringComparison.OrdinalIgnoreCase) ||
                    auth.DeviceType != device.DeviceType)
                    throw new InvalidDataException("Current device identity conflicts with immutable membership history.");
            }
            currentRemoteDevices.Add(device);
        }

        return new ValidatedEnrollmentGraph(userSnapshot, controlState, controlItems, pendingItems, currentRemoteDevices);
    }

    private void VerifyUserSnapshot(DeviceEnrollmentUserSnapshot source)
    {
        SyncVersionStampComparer.Validate(source.GeneralUserDataVersion);
        if (source.UsernameHash.Length != CryptographyConstants.Sha256HashSizeInBytes ||
            source.UsernameSalt.Length != CryptographyConstants.Sha256HashSizeInBytes ||
            source.EncryptedPayload.Length == 0 || source.EncryptedGeneralUserDataPayload.Length == 0 ||
            source.EncryptedUserPasswordsDataPayload.Length == 0 || source.EncryptedUserDevicesDataPayload.Length == 0 ||
            source.IntegrityHash.Length != CryptographyConstants.Sha256HashSizeInBytes)
            throw new InvalidDataException("The enrollment bootstrap contains incomplete encrypted user data.");
        var user = new User();
        ApplyUser(source, user);
        if (!Hashing.Verify(source.IntegrityHash, user.IntegrityHash))
            throw new InvalidDataException("The enrollment bootstrap user integrity hash is invalid for its real epochs.");
    }

    private void ApplyUser(DeviceEnrollmentUserSnapshot source, User target)
    {
        target.UId = source.UId;
        target.UsernameHash = source.UsernameHash.ToArray();
        target.UsernameSalt = source.UsernameSalt.ToArray();
        target.SetGeneralUserDataVersion(source.GeneralUserDataVersion);
        target.PasswordSalt = source.PasswordSalt.ToArray();
        target.EncryptedPayload = source.EncryptedPayload.ToArray();
        target.EncryptedGeneralUserDataPayload = source.EncryptedGeneralUserDataPayload.ToArray();
        target.EncryptedUserPasswordsDataPayload = source.EncryptedUserPasswordsDataPayload.ToArray();
        target.EncryptedUserDevicesDataPayload = source.EncryptedUserDevicesDataPayload.ToArray();
        target.SavedKey = null;
        target.KeyEpoch = source.KeyEpoch;
        target.MembershipEpoch = source.MembershipEpoch;
        target.LastModifiedAt = UtcDateTimeUtil.ToUtc(source.LastModifiedAt);
        target.UserDataLastModifiedAt = UtcDateTimeUtil.ToUtc(source.UserDataLastModifiedAt);
        target.GeneralUserDataLastModifiedAt = UtcDateTimeUtil.ToUtc(source.GeneralUserDataLastModifiedAt);
        target.UserPasswordsDataLastModifiedAt = UtcDateTimeUtil.ToUtc(source.UserPasswordsDataLastModifiedAt);
        target.UserDevicesDataLastModifiedAt = UtcDateTimeUtil.ToUtc(source.UserDevicesDataLastModifiedAt);
        target.GenerateIntegrityHash();
    }

    private void ValidateAuthorization(DeviceEnrollmentMembershipAuthorizationSnapshot row, Guid userId)
    {
        if (row.AuthorizationId == Guid.Empty || row.UserId != userId || row.DeviceId == Guid.Empty || row.OriginInstanceId == Guid.Empty ||
            row.SignPublicKey.Length == 0 || row.SignPublicKeyHash.Length != CryptographyConstants.Sha256HashSizeInBytes ||
            row.AgreementPublicKeyHash.Length != CryptographyConstants.Sha256HashSizeInBytes ||
            !Hashing.Verify(row.SignPublicKeyHash, Hashing.SHA256Hash(row.SignPublicKey)) ||
            string.IsNullOrWhiteSpace(row.TlsCertFingerprint) || !DeviceTypeDetector.IsValid(row.DeviceType) ||
            row.StartedMembershipEpoch <= 0 || row.MinimumKeyEpoch <= 0 ||
            (row.EndedMembershipEpoch.HasValue && row.EndedMembershipEpoch <= row.StartedMembershipEpoch) ||
            row.IsActive == row.EndedMembershipEpoch.HasValue ||
            (!row.IsGenesis && (!row.AdditionOperationId.HasValue || row.AdditionOperationHash is not { Length: CryptographyConstants.Sha256HashSizeInBytes })) ||
            (row.IsGenesis && row.StartedMembershipEpoch != 1) ||
            (!row.IsActive && (!row.RemovalOperationId.HasValue || row.RemovalOperationHash is not { Length: CryptographyConstants.Sha256HashSizeInBytes })))
            throw new InvalidDataException("The enrollment bootstrap contains invalid membership history.");
    }

    private void ValidateCutoff(DeviceEnrollmentRemovalCutoffSnapshot row, IReadOnlyDictionary<Guid, DeviceEnrollmentMembershipAuthorizationSnapshot> authById)
    {
        if (row.CutoffId == Guid.Empty || row.UserId == Guid.Empty || row.DeviceId == Guid.Empty || row.OriginInstanceId == Guid.Empty ||
            row.UserKeyEpoch <= 0 || row.HighestAcceptedSnapshotRevision < 0 || row.HighestAcceptedControlSequence < 0 ||
            row.ResultingMembershipEpoch <= 1 || row.RemovalOperationId == Guid.Empty ||
            row.RemovalOperationHash.Length != CryptographyConstants.Sha256HashSizeInBytes || !authById.TryGetValue(row.AuthorizationId, out var auth) ||
            auth.UserId != row.UserId || auth.DeviceId != row.DeviceId || auth.OriginInstanceId != row.OriginInstanceId || auth.IsActive ||
            auth.RemovalOperationId != row.RemovalOperationId || auth.RemovalOperationHash is null || !Hashing.Verify(auth.RemovalOperationHash, row.RemovalOperationHash))
            throw new InvalidDataException("The enrollment bootstrap contains an invalid removal cutoff.");
    }

    private UserMembershipAuthorization FindAuthorization(IEnumerable<DeviceEnrollmentMembershipAuthorizationSnapshot> rows, Guid deviceId, Guid originId, long membershipEpoch, long keyEpoch)
    {
        var row = rows.SingleOrDefault(item => item.DeviceId == deviceId && item.OriginInstanceId == originId &&
            item.StartedMembershipEpoch <= membershipEpoch && (!item.EndedMembershipEpoch.HasValue || membershipEpoch < item.EndedMembershipEpoch.Value) &&
            item.MinimumKeyEpoch <= keyEpoch && (!item.MaximumKeyEpoch.HasValue || keyEpoch <= item.MaximumKeyEpoch.Value))
            ?? throw new InvalidDataException("An immutable envelope author was never authorized for the signed epoch and key namespace.");
        return ToAuthorization(row);
    }

    private void ValidateControlPayload(UserControlOperationEnvelope envelope)
    {
        switch (envelope.OperationType)
        {
            case UserControlOperationType.KeyEpochReplacement:
            {
                var payload = UserControlOperationEnvelopeUtil.DeserializeKeyEpochReplacementPayload(envelope.OperationPayload);
                if (payload.UserId != envelope.UserId || payload.PreviousKeyEpoch != envelope.PreviousKeyEpoch || payload.ResultingKeyEpoch != envelope.ResultingKeyEpoch ||
                    payload.MembershipEpoch != envelope.PreviousMembershipEpoch)
                    throw new InvalidDataException("A key-epoch payload does not match its immutable header.");
                break;
            }
            case UserControlOperationType.DeviceAddition:
            {
                var payload = UserControlOperationEnvelopeUtil.DeserializeDeviceAdditionPayload(envelope.OperationPayload);
                if (payload.UserId != envelope.UserId || payload.KeyEpoch != envelope.PreviousKeyEpoch || payload.PreviousMembershipEpoch != envelope.PreviousMembershipEpoch ||
                    payload.ResultingMembershipEpoch != envelope.ResultingMembershipEpoch)
                    throw new InvalidDataException("A device-addition payload does not match its immutable header.");
                break;
            }
            case UserControlOperationType.DeviceRemoval:
            {
                var payload = UserControlOperationEnvelopeUtil.DeserializeDeviceRemovalPayload(envelope.OperationPayload);
                if (payload.UserId != envelope.UserId || payload.KeyEpoch != envelope.PreviousKeyEpoch || payload.PreviousMembershipEpoch != envelope.PreviousMembershipEpoch ||
                    payload.ResultingMembershipEpoch != envelope.ResultingMembershipEpoch)
                    throw new InvalidDataException("A device-removal payload does not match its immutable header.");
                break;
            }
            default:
                throw new InvalidDataException("Unsupported control operation in enrollment bootstrap.");
        }
    }

    private void ValidateAppliedTransitionChains(IReadOnlyList<ValidatedControlOperation> operations, DeviceEnrollmentUserSnapshot user)
    {
        var membership = operations.Where(item => item.Snapshot.Status == UserControlOperationStatus.Applied &&
            item.Envelope.OperationType is UserControlOperationType.DeviceAddition or UserControlOperationType.DeviceRemoval).ToList();
        for (var epoch = 1L; epoch < user.MembershipEpoch; epoch++)
        {
            if (membership.Count(item => item.Envelope.PreviousMembershipEpoch == epoch) != 1)
                throw new InvalidDataException("The applied membership transition history is incomplete or ambiguous.");
        }
        if (membership.Any(item => item.Envelope.ResultingMembershipEpoch > user.MembershipEpoch))
            throw new InvalidDataException("The membership transition history is ahead of canonical state.");

        var key = operations.Where(item => item.Snapshot.Status == UserControlOperationStatus.Applied && item.Envelope.OperationType == UserControlOperationType.KeyEpochReplacement).ToList();
        for (var epoch = 1L; epoch < user.KeyEpoch; epoch++)
        {
            if (key.Count(item => item.Envelope.PreviousKeyEpoch == epoch) != 1)
                throw new InvalidDataException("The applied key transition history is incomplete or ambiguous.");
        }
    }

    private void ValidateDeviceSnapshot(DeviceEnrollmentDeviceSnapshot source)
    {
        if (source.Id == Guid.Empty || source.PublicKey.Length == 0 || source.SignPublicKey.Length == 0 ||
            string.IsNullOrWhiteSpace(source.TlsCertFingerprint) || !DeviceTypeDetector.IsValid(source.DeviceType))
            throw new InvalidDataException("The enrollment bootstrap contains an incomplete current device identity.");
        var device = new Device
        {
            Id = source.Id, PublicKey = source.PublicKey.ToArray(), SignPublicKey = source.SignPublicKey.ToArray(),
            TlsCertFingerprint = SyncIdentityUtil.NormalizeFingerprint(source.TlsCertFingerprint), DeviceType = source.DeviceType,
            LastKnownHash = source.LastKnownHash.ToArray(), LastSync = UtcDateTimeUtil.ToUtc(source.LastSync), LastSeen = UtcDateTimeUtil.ToUtc(source.LastSeen),
            IsTrusted = source.IsTrusted, IsBlocked = source.IsBlocked, BlockedReason = source.BlockedReason, BlockedAt = UtcDateTimeUtil.ToUtc(source.BlockedAt),
            InvalidSyncAttemptCount = source.InvalidSyncAttemptCount, LastInvalidSyncAttemptAt = UtcDateTimeUtil.ToUtc(source.LastInvalidSyncAttemptAt),
            LastModifiedAt = UtcDateTimeUtil.ToUtc(source.LastModifiedAt)
        };
        device.GenerateIntegrityHash();
        if (source.IntegrityHash.Length != CryptographyConstants.Sha256HashSizeInBytes || !Hashing.Verify(source.IntegrityHash, device.IntegrityHash))
            throw new InvalidDataException("The enrollment bootstrap current device integrity hash is invalid.");
    }

    private async Task UpsertRemoteDeviceAsync(IDeviceRepository devices, IUserDeviceRepository userDevices, DeviceEnrollmentDeviceSnapshot source, CancellationToken ct)
    {
        var existing = await devices.GetByIdAsync(source.Id, ct);
        if (existing is null)
        {
            existing = new Device { Id = source.Id };
            await devices.AddAsync(existing, ct);
        }
        else
        {
            var identityDiffers = (!existing.SignPublicKey.SequenceEqual(source.SignPublicKey) && existing.SignPublicKey.Length != 0) ||
                (!existing.PublicKey.SequenceEqual(source.PublicKey) && existing.PublicKey.Length != 0) ||
                (!string.Equals(SyncIdentityUtil.NormalizeFingerprint(existing.TlsCertFingerprint), SyncIdentityUtil.NormalizeFingerprint(source.TlsCertFingerprint), StringComparison.OrdinalIgnoreCase) && !string.IsNullOrWhiteSpace(existing.TlsCertFingerprint)) ||
                existing.DeviceType != source.DeviceType;
            if (identityDiffers && await userDevices.HasAnyActiveLinkForDeviceAsync(source.Id, ct))
                throw new InvalidDataException("A current remote device conflicts with an identity still used by an active membership link.");
        }
        existing.PublicKey = source.PublicKey.ToArray();
        existing.SignPublicKey = source.SignPublicKey.ToArray();
        existing.TlsCertFingerprint = SyncIdentityUtil.NormalizeFingerprint(source.TlsCertFingerprint);
        existing.DeviceType = source.DeviceType;
        existing.LastKnownHash = source.LastKnownHash.ToArray();
        existing.LastSync = UtcDateTimeUtil.ToUtc(source.LastSync);
        existing.LastSeen = UtcDateTimeUtil.ToUtc(source.LastSeen);
        existing.IsTrusted = true;
        existing.IsBlocked = source.IsBlocked;
        existing.BlockedReason = source.BlockedReason;
        existing.BlockedAt = UtcDateTimeUtil.ToUtc(source.BlockedAt);
        existing.InvalidSyncAttemptCount = source.InvalidSyncAttemptCount;
        existing.LastInvalidSyncAttemptAt = UtcDateTimeUtil.ToUtc(source.LastInvalidSyncAttemptAt);
        existing.LastModifiedAt = UtcDateTimeUtil.ToUtc(source.LastModifiedAt);
        existing.GenerateIntegrityHash();
    }

    private UserSyncSnapshot CreateSnapshotRow(ValidatedPendingSnapshot item, UserSnapshotEnvelope envelope) => new()
    {
        UserId = envelope.UserId,
        OriginDeviceId = envelope.OriginDeviceId,
        OriginInstanceId = envelope.OriginInstanceId,
        OriginRevision = envelope.OriginRevision,
        UserKeyEpoch = envelope.UserKeyEpoch,
        MembershipEpoch = envelope.MembershipEpoch,
        CreatedAtUtc = envelope.CreatedAtUtc,
        ReceivedAtUtc = UtcDateTimeUtil.ToUtc(item.Snapshot.ReceivedAtUtc),
        SnapshotHash = envelope.SnapshotHash.ToArray(),
        OriginSignPublicKey = envelope.OriginSignPublicKey.ToArray(),
        OriginSignature = envelope.OriginSignature.ToArray(),
        EnvelopePayload = item.Snapshot.EnvelopePayload.ToArray(),
        Status = item.Snapshot.Status == UserSyncSnapshotStatus.LocalPublished
            ? UserSyncSnapshotStatus.MergedReceipt
            : item.Snapshot.Status,
        QuarantineReason = item.Snapshot.QuarantineReason,
        ConflictingSnapshotHash = item.Snapshot.ConflictingSnapshotHash?.ToArray()
    };

    private void CopySnapshotRow(UserSyncSnapshot source, UserSyncSnapshot target)
    {
        target.OriginRevision = source.OriginRevision;
        target.MembershipEpoch = source.MembershipEpoch;
        target.CreatedAtUtc = source.CreatedAtUtc;
        target.ReceivedAtUtc = source.ReceivedAtUtc;
        target.SnapshotHash = source.SnapshotHash;
        target.OriginSignPublicKey = source.OriginSignPublicKey;
        target.OriginSignature = source.OriginSignature;
        target.EnvelopePayload = source.EnvelopePayload;
        target.Status = source.Status;
        target.QuarantineReason = source.QuarantineReason;
        target.ConflictingSnapshotHash = source.ConflictingSnapshotHash;
    }

    private bool HashesEqual(byte[]? left, byte[]? right)
    {
        if (left is null || right is null)
            return left is null && right is null;
        return Hashing.Verify(left, right);
    }

    private UserMembershipAuthorization ToAuthorization(DeviceEnrollmentMembershipAuthorizationSnapshot row) => new()
    {
        AuthorizationId = row.AuthorizationId, UserId = row.UserId, DeviceId = row.DeviceId, OriginInstanceId = row.OriginInstanceId,
        SignPublicKey = row.SignPublicKey.ToArray(), SignPublicKeyHash = row.SignPublicKeyHash.ToArray(), AgreementPublicKeyHash = row.AgreementPublicKeyHash.ToArray(),
        TlsCertFingerprint = SyncIdentityUtil.NormalizeFingerprint(row.TlsCertFingerprint), DeviceType = row.DeviceType,
        StartedMembershipEpoch = row.StartedMembershipEpoch, EndedMembershipEpoch = row.EndedMembershipEpoch,
        MinimumKeyEpoch = row.MinimumKeyEpoch, MaximumKeyEpoch = row.MaximumKeyEpoch, IsActive = row.IsActive, IsGenesis = row.IsGenesis,
        AdditionOperationId = row.AdditionOperationId, AdditionOperationHash = row.AdditionOperationHash?.ToArray(),
        RemovalOperationId = row.RemovalOperationId, RemovalOperationHash = row.RemovalOperationHash?.ToArray(),
        CreatedAtUtc = UtcDateTimeUtil.ToUtc(row.CreatedAtUtc), EndedAtUtc = UtcDateTimeUtil.ToUtc(row.EndedAtUtc)
    };

    private UserOriginRemovalCutoff ToCutoff(DeviceEnrollmentRemovalCutoffSnapshot row) => new()
    {
        CutoffId = row.CutoffId, UserId = row.UserId, DeviceId = row.DeviceId, OriginInstanceId = row.OriginInstanceId,
        UserKeyEpoch = row.UserKeyEpoch, HighestAcceptedSnapshotRevision = row.HighestAcceptedSnapshotRevision,
        HighestAcceptedControlSequence = row.HighestAcceptedControlSequence, ResultingMembershipEpoch = row.ResultingMembershipEpoch,
        AuthorizationId = row.AuthorizationId, RemovalOperationId = row.RemovalOperationId,
        RemovalOperationHash = row.RemovalOperationHash.ToArray(), CreatedAtUtc = UtcDateTimeUtil.ToUtc(row.CreatedAtUtc)
    };

    private void RequireSameAuthorization(UserMembershipAuthorization existing, DeviceEnrollmentMembershipAuthorizationSnapshot row)
    {
        var imported = ToAuthorization(row);
        if (existing.UserId != imported.UserId || existing.DeviceId != imported.DeviceId || existing.OriginInstanceId != imported.OriginInstanceId ||
            !existing.SignPublicKey.SequenceEqual(imported.SignPublicKey) || existing.StartedMembershipEpoch != imported.StartedMembershipEpoch ||
            existing.EndedMembershipEpoch != imported.EndedMembershipEpoch || existing.IsActive != imported.IsActive ||
            existing.AdditionOperationId != imported.AdditionOperationId || existing.RemovalOperationId != imported.RemovalOperationId)
            throw new InvalidDataException("A locally retained membership authorization conflicts with the enrollment bootstrap.");
    }

    private void RequireSameCutoff(UserOriginRemovalCutoff existing, DeviceEnrollmentRemovalCutoffSnapshot row)
    {
        if (existing.CutoffId != row.CutoffId || existing.AuthorizationId != row.AuthorizationId || existing.RemovalOperationId != row.RemovalOperationId ||
            existing.HighestAcceptedSnapshotRevision != row.HighestAcceptedSnapshotRevision || existing.HighestAcceptedControlSequence != row.HighestAcceptedControlSequence ||
            !Hashing.Verify(existing.RemovalOperationHash, row.RemovalOperationHash))
            throw new InvalidDataException("A locally retained removal cutoff conflicts with the enrollment bootstrap.");
    }

}
