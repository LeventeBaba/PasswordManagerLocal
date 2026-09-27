using PasswordManagerLocal.Common.Backend.Abstractions.Persistence;
using PasswordManagerLocal.Common.Backend.Abstractions.Repositories;
using PasswordManagerLocal.Common.Backend.Abstractions.Services;
using PasswordManagerLocal.Common.Backend.Models;
using PasswordManagerLocal.Common.Backend.Sync;
using PasswordManagerLocal.Common.Backend.Utils;
using Microsoft.EntityFrameworkCore;

namespace PasswordManagerLocal.Common.Backend.Services;

public sealed class UserControlOperationInboxService : IUserControlOperationInboxService
{
    private readonly IUserControlOperationRepository _operations;
    private readonly IUserControlStateRepository _states;
    private readonly IUserRepository _users;
    private readonly IDeviceRepository _devices;
    private readonly IUserDeviceRepository _userDevices;
    private readonly IUserSyncSnapshotRepository _snapshots;
    private readonly IUserLifecycleCoordinator _lifecycle;
    private readonly IInteractiveSessionStateService _interactiveSessions;
    private readonly IUnitOfWork _uow;
    private readonly IUserMembershipAuthorizationService _membershipAuthorization;
    private readonly IUserMembershipAuthorizationRepository _authorizationRows;
    private readonly ILocalUserDeviceRepository _localUsers;
    private readonly IDeviceIdentityService _identity;
    private readonly ISyncRuntimeService _syncRuntime;
    private readonly IDeletedUserBarrierRepository? _deletionBarriers;
    private readonly IUserAccountDeletionCleanupService? _deletionCleanup;
    private readonly IDeviceEnrollmentService? _enrollment;
    private readonly IUserLoginIdentityProjectionService? _loginIdentities;
    private readonly ISyncVersionClockService? _versionClock;
    private readonly IUserCanonicalHealthService? _canonicalHealth;
    private readonly IUserSyncFaultService? _syncFaults;

    public UserControlOperationInboxService(
        IUserControlOperationRepository operations,
        IUserControlStateRepository states,
        IUserRepository users,
        IDeviceRepository devices,
        IUserDeviceRepository userDevices,
        IUserSyncSnapshotRepository snapshots,
        IUserLifecycleCoordinator lifecycle,
        IInteractiveSessionStateService interactiveSessions,
        IUnitOfWork uow,
        IUserMembershipAuthorizationService membershipAuthorization,
        IUserMembershipAuthorizationRepository authorizationRows,
        ILocalUserDeviceRepository localUsers,
        IDeviceIdentityService identity,
        ISyncRuntimeService syncRuntime,
        IDeletedUserBarrierRepository? deletionBarriers = null,
        IUserAccountDeletionCleanupService? deletionCleanup = null,
        IDeviceEnrollmentService? enrollment = null,
        IUserLoginIdentityProjectionService? loginIdentities = null,
        ISyncVersionClockService? versionClock = null,
        IUserCanonicalHealthService? canonicalHealth = null,
        IUserSyncFaultService? syncFaults = null)
    {
        _operations = operations;
        _states = states;
        _users = users;
        _devices = devices;
        _userDevices = userDevices;
        _snapshots = snapshots;
        _lifecycle = lifecycle;
        _interactiveSessions = interactiveSessions;
        _uow = uow;
        _membershipAuthorization = membershipAuthorization;
        _authorizationRows = authorizationRows;
        _localUsers = localUsers;
        _identity = identity;
        _syncRuntime = syncRuntime;
        _deletionBarriers = deletionBarriers;
        _deletionCleanup = deletionCleanup;
        _enrollment = enrollment;
        _loginIdentities = loginIdentities;
        _versionClock = versionClock;
        _canonicalHealth = canonicalHealth;
        _syncFaults = syncFaults;
    }

    public async Task<UserControlOperationReceiptResult> StoreAndApplyAsync(
        UserControlOperationEnvelope envelope,
        Guid transportPeerDeviceId,
        CancellationToken ct = default)
    {
        UserControlOperationEnvelopeUtil.ValidateStructureAndHash(envelope);
        if (transportPeerDeviceId == Guid.Empty)
            throw new InvalidDataException("The transport peer identity is missing.");

        return await _lifecycle.ExecuteAsync(
            envelope.UserId,
            async token =>
            {
                var stored = await StoreCoreAsync(envelope, transportPeerDeviceId, token);
                if (stored.State is UserControlOperationReceiptState.Quarantined or UserControlOperationReceiptState.Rejected or UserControlOperationReceiptState.Obsolete)
                {
                    if (stored.State == UserControlOperationReceiptState.Quarantined)
                        await RecordControlFaultAsync(envelope, stored.Detail, token);
                    return stored;
                }

                if (envelope.OperationType == UserControlOperationType.AccountDeletion)
                {
                    // A verified barrier was committed with the StoredPending row. Invalidate all
                    // process-local access before cleanup so any retryable failure remains fail-closed.
                    await _interactiveSessions.LogoutUserAsync(
                        envelope.UserId,
                        AuthSessionInvalidationReason.ProfileRemoved,
                        CancellationToken.None);
                    if (_enrollment is not null)
                    {
                        try { await _enrollment.CancelEnrollmentAsync(CancellationToken.None); }
                        catch { }
                    }
                }

                var applied = await TryApplyStoredSafelyAsync(envelope.OperationId, token);
                if (applied.State == UserControlOperationReceiptState.Quarantined)
                    await RecordControlFaultAsync(envelope, applied.Detail, token);
                return applied;
            },
            ct);
    }

    public async Task<UserControlOperationReceiptResult> TryApplyStoredAsync(
        Guid operationId,
        CancellationToken ct = default)
    {
        var row = await _operations.GetByIdAsync(operationId, ct)
            ?? throw new InvalidOperationException("The stored control operation does not exist.");
        return await _lifecycle.ExecuteAsync(
            row.UserId,
            async token =>
            {
                var result = await TryApplyStoredSafelyAsync(operationId, token);
                if (result.State == UserControlOperationReceiptState.Quarantined)
                    await RecordControlFaultAsync(UserControlOperationEnvelopeUtil.Deserialize(row.EnvelopePayload), result.Detail, token);
                return result;
            },
            ct);
    }

    private async Task<UserControlOperationReceiptResult> StoreCoreAsync(
        UserControlOperationEnvelope envelope,
        Guid transportPeerDeviceId,
        CancellationToken ct)
    {
        var isAccountDeletion = envelope.OperationType == UserControlOperationType.AccountDeletion;
        AccountDeletionPayload? deletionPayload = null;
        if (!isAccountDeletion && _deletionBarriers is not null &&
            await _deletionBarriers.ExistsAsync(envelope.UserId, ct))
        {
            return Receipt(envelope, UserControlOperationReceiptState.Obsolete,
                "The account identity is permanently deleted; earlier lifecycle operations are obsolete.");
        }

        try
        {
            await _membershipAuthorization.VerifyControlAuthorAsync(envelope, ct);
            if (isAccountDeletion)
            {
                deletionPayload = UserControlOperationEnvelopeUtil.DeserializeAccountDeletionPayload(envelope.OperationPayload);
                DeletedUserBarrierUtil.ValidateEnvelopePayloadMatch(envelope, deletionPayload);
            }
        }
        catch (Exception ex) when (ex is InvalidDataException or UnauthorizedAccessException)
        {
            return Receipt(envelope, UserControlOperationReceiptState.Rejected, ex.Message);
        }

        var user = await _users.GetByIdAsync(envelope.UserId, ct);
        if (user is null && !isAccountDeletion)
            return Receipt(envelope, UserControlOperationReceiptState.Rejected, "The canonical account does not exist locally.");

        var serialized = UserControlOperationEnvelopeUtil.Serialize(envelope);

        var existing = await _operations.GetByIdAsync(envelope.OperationId, ct);
        if (existing is not null)
        {
            if (!HashEquals(existing.OperationHash, envelope.OperationHash))
            {
                await MarkConflictAsync(
                    existing,
                    envelope.OperationId,
                    envelope.OperationHash,
                    "The same control-operation id was received with a different immutable hash.",
                    ct);
                await _uow.SaveChangesAsync(ct);
                return Receipt(envelope, UserControlOperationReceiptState.Quarantined, existing.StatusReason);
            }

            existing.ReceivedAtUtc = DateTimeOffset.UtcNow;
            existing.LastReceivedFromDeviceId = transportPeerDeviceId;
            _operations.Update(existing);
            if (isAccountDeletion)
                await InstallVerifiedDeletionBarrierAsync(envelope, deletionPayload!, ct);
            await _uow.SaveChangesAsync(ct);
            return Receipt(
                envelope,
                ToExistingReceiptState(existing.Status),
                existing.StatusReason);
        }

        var sameSequence = await _operations.GetByOriginSequenceAsync(
            envelope.UserId,
            envelope.OriginDeviceId,
            envelope.OriginInstanceId,
            envelope.OriginSequence,
            ct);
        if (sameSequence is not null)
        {
            await MarkConflictAsync(
                sameSequence,
                envelope.OperationId,
                envelope.OperationHash,
                "The same original-author control sequence was received with incompatible content.",
                ct);
            await _uow.SaveChangesAsync(ct);
            return Receipt(envelope, UserControlOperationReceiptState.Quarantined, sameSequence.StatusReason);
        }

        var state = await _states.GetAsync(envelope.UserId, ct);
        if (!isAccountDeletion && state?.HasConflict == true)
        {
            var reason = state.ConflictReason ?? "The account control plane is quarantined.";
            var quarantined = UserControlOperationMapping.ToStoredOperation(
                envelope,
                serialized,
                UserControlOperationStatus.Quarantined,
                DateTimeOffset.UtcNow,
                null,
                transportPeerDeviceId);
            quarantined.StatusReason = Truncate(reason);
            quarantined.ConflictingOperationHash = state.ConflictingOperationHash?.ToArray();
            await _operations.AddAsync(quarantined, ct);
            await _uow.SaveChangesAsync(ct);
            return Receipt(envelope, UserControlOperationReceiptState.Quarantined, reason);
        }

        if (envelope.OperationType == UserControlOperationType.KeyEpochReplacement)
        {
            var sameBase = await _operations.ListKeyTransitionsFromAsync(envelope.UserId, envelope.PreviousKeyEpoch, ct);
            if (sameBase.Count != 0)
            {
                var reason = "Multiple authoritative key-epoch operations claim the same base transition.";
                foreach (var conflict in sameBase)
                    Quarantine(conflict, envelope.OperationHash, reason);

                var incomingConflict = UserControlOperationMapping.ToStoredOperation(
                    envelope,
                    serialized,
                    UserControlOperationStatus.Quarantined,
                    DateTimeOffset.UtcNow,
                    null,
                    transportPeerDeviceId);
                incomingConflict.StatusReason = reason;
                incomingConflict.ConflictingOperationHash = sameBase[0].OperationHash.ToArray();
                await _operations.AddAsync(incomingConflict, ct);

                state ??= await GetOrCreateStateAsync(user!, ct);
                SetStateConflict(state, envelope.OperationId, envelope.OperationHash, reason);
                await _uow.SaveChangesAsync(ct);
                return Receipt(envelope, UserControlOperationReceiptState.Quarantined, reason);
            }
        }
        else if (envelope.OperationType is UserControlOperationType.DeviceAddition or UserControlOperationType.DeviceRemoval)
        {
            var sameBase = await _operations.ListMembershipTransitionsFromAsync(envelope.UserId, envelope.PreviousMembershipEpoch, ct);
            if (sameBase.Count != 0)
            {
                var reason = "Multiple authoritative membership operations claim the same base transition.";
                foreach (var conflict in sameBase)
                    Quarantine(conflict, envelope.OperationHash, reason);
                var incomingConflict = UserControlOperationMapping.ToStoredOperation(
                    envelope,
                    serialized,
                    UserControlOperationStatus.Quarantined,
                    DateTimeOffset.UtcNow,
                    null,
                    transportPeerDeviceId);
                incomingConflict.StatusReason = reason;
                incomingConflict.ConflictingOperationHash = sameBase[0].OperationHash.ToArray();
                await _operations.AddAsync(incomingConflict, ct);
                state ??= await GetOrCreateStateAsync(user!, ct);
                SetStateConflict(state, envelope.OperationId, envelope.OperationHash, reason);
                await _uow.SaveChangesAsync(ct);
                return Receipt(envelope, UserControlOperationReceiptState.Quarantined, reason);
            }
        }

        var incomingRow = UserControlOperationMapping.ToStoredOperation(
            envelope,
            serialized,
            UserControlOperationStatus.StoredPending,
            DateTimeOffset.UtcNow,
            null,
            transportPeerDeviceId);
        await _operations.AddAsync(incomingRow, ct);
        if (isAccountDeletion)
            await InstallVerifiedDeletionBarrierAsync(envelope, deletionPayload!, ct);
        try
        {
            await _uow.SaveChangesAsync(ct);
            return Receipt(envelope, UserControlOperationReceiptState.StoredPending);
        }
        catch (DbUpdateException ex)
        {
            _uow.ClearTrackedChanges();
            return await ResolveConcurrentStoreAsync(envelope, transportPeerDeviceId, ex, ct);
        }
    }

    private async Task<UserControlOperationReceiptResult> ResolveConcurrentStoreAsync(
        UserControlOperationEnvelope envelope,
        Guid transportPeerDeviceId,
        DbUpdateException originalException,
        CancellationToken ct)
    {
        var existing = await _operations.GetByIdAsync(envelope.OperationId, ct);
        if (existing is not null)
        {
            if (!HashEquals(existing.OperationHash, envelope.OperationHash))
            {
                await MarkConflictAsync(
                    existing,
                    envelope.OperationId,
                    envelope.OperationHash,
                    "The same control-operation id raced with a different immutable hash.",
                    ct);
                await _uow.SaveChangesAsync(ct);
                return Receipt(envelope, UserControlOperationReceiptState.Quarantined, existing.StatusReason);
            }

            existing.ReceivedAtUtc = DateTimeOffset.UtcNow;
            existing.LastReceivedFromDeviceId = transportPeerDeviceId;
            _operations.Update(existing);
            if (envelope.OperationType == UserControlOperationType.AccountDeletion)
            {
                var payload = UserControlOperationEnvelopeUtil.DeserializeAccountDeletionPayload(envelope.OperationPayload);
                DeletedUserBarrierUtil.ValidateEnvelopePayloadMatch(envelope, payload);
                await InstallVerifiedDeletionBarrierAsync(envelope, payload, ct);
            }
            await _uow.SaveChangesAsync(ct);
            return Receipt(
                envelope,
                ToExistingReceiptState(existing.Status),
                existing.StatusReason);
        }

        var sameSequence = await _operations.GetByOriginSequenceAsync(
            envelope.UserId,
            envelope.OriginDeviceId,
            envelope.OriginInstanceId,
            envelope.OriginSequence,
            ct);
        if (sameSequence is not null)
        {
            await MarkConflictAsync(
                sameSequence,
                envelope.OperationId,
                envelope.OperationHash,
                "The same original-author control sequence raced with incompatible content.",
                ct);
            await _uow.SaveChangesAsync(ct);
            return Receipt(envelope, UserControlOperationReceiptState.Quarantined, sameSequence.StatusReason);
        }

        if (envelope.OperationType == UserControlOperationType.KeyEpochReplacement)
        {
            var sameBase = await _operations.ListKeyTransitionsFromAsync(envelope.UserId, envelope.PreviousKeyEpoch, ct);
            if (sameBase.Count != 0)
            {
                const string reason = "Concurrent authoritative key-epoch operations claimed the same base transition.";
                foreach (var conflict in sameBase)
                    Quarantine(conflict, envelope.OperationHash, reason);

                var serialized = UserControlOperationEnvelopeUtil.Serialize(envelope);
                var incomingConflict = UserControlOperationMapping.ToStoredOperation(
                    envelope,
                    serialized,
                    UserControlOperationStatus.Quarantined,
                    DateTimeOffset.UtcNow,
                    null,
                    transportPeerDeviceId);
                incomingConflict.StatusReason = reason;
                incomingConflict.ConflictingOperationHash = sameBase[0].OperationHash.ToArray();
                await _operations.AddAsync(incomingConflict, ct);

                var user = await _users.GetByIdAsync(envelope.UserId, ct);
                var state = await _states.GetAsync(envelope.UserId, ct);
                if (state is null)
                {
                    state = new UserControlState
                    {
                        UserId = envelope.UserId,
                        LocalOriginInstanceId = Guid.Empty,
                        NextOriginSequence = 1,
                        AppliedKeyEpoch = user?.KeyEpoch ?? 0,
                        AppliedMembershipEpoch = user?.MembershipEpoch ?? 0,
                        LastUpdatedAtUtc = DateTimeOffset.UtcNow
                    };
                    await _states.AddAsync(state, ct);
                }
                SetStateConflict(state, envelope.OperationId, envelope.OperationHash, reason);
                await _uow.SaveChangesAsync(ct);
                return Receipt(envelope, UserControlOperationReceiptState.Quarantined, reason);
            }
        }

        if (envelope.OperationType is UserControlOperationType.DeviceAddition or UserControlOperationType.DeviceRemoval)
        {
            var sameBase = await _operations.ListMembershipTransitionsFromAsync(envelope.UserId, envelope.PreviousMembershipEpoch, ct);
            if (sameBase.Count != 0)
            {
                const string reason = "Concurrent authoritative membership operations claimed the same base transition.";
                foreach (var conflict in sameBase)
                    Quarantine(conflict, envelope.OperationHash, reason);

                var serialized = UserControlOperationEnvelopeUtil.Serialize(envelope);
                var incomingConflict = UserControlOperationMapping.ToStoredOperation(
                    envelope,
                    serialized,
                    UserControlOperationStatus.Quarantined,
                    DateTimeOffset.UtcNow,
                    null,
                    transportPeerDeviceId);
                incomingConflict.StatusReason = reason;
                incomingConflict.ConflictingOperationHash = sameBase[0].OperationHash.ToArray();
                await _operations.AddAsync(incomingConflict, ct);

                var user = await _users.GetByIdAsync(envelope.UserId, ct);
                var state = await _states.GetAsync(envelope.UserId, ct);
                if (state is null)
                {
                    state = new UserControlState
                    {
                        UserId = envelope.UserId,
                        LocalOriginInstanceId = Guid.Empty,
                        NextOriginSequence = 1,
                        AppliedKeyEpoch = user?.KeyEpoch ?? 0,
                        AppliedMembershipEpoch = user?.MembershipEpoch ?? 0,
                        LastUpdatedAtUtc = DateTimeOffset.UtcNow
                    };
                    await _states.AddAsync(state, ct);
                }
                SetStateConflict(state, envelope.OperationId, envelope.OperationHash, reason);
                await _uow.SaveChangesAsync(ct);
                return Receipt(envelope, UserControlOperationReceiptState.Quarantined, reason);
            }
        }

        throw new InvalidOperationException(
            "The control operation could not be stored because a database concurrency constraint was violated.",
            originalException);
    }

    private async Task InstallVerifiedDeletionBarrierAsync(
        UserControlOperationEnvelope envelope,
        AccountDeletionPayload payload,
        CancellationToken ct)
    {
        if (_deletionBarriers is null)
            throw new InvalidOperationException("Authoritative account-deletion barrier storage is not registered.");

        var barrier = await _deletionBarriers.GetAsync(envelope.UserId, ct);
        if (barrier is null)
        {
            await _deletionBarriers.AddAsync(DeletedUserBarrierUtil.Create(envelope, payload, DateTimeOffset.UtcNow), ct);
            return;
        }

        if (DeletedUserBarrierUtil.Matches(barrier, envelope))
            return;

        var previousCanonicalOperationId = barrier.DeletionOperationId;
        var previousCanonicalOperationHash = barrier.OperationHash.ToArray();
        var candidateBecomesCanonical = DeletedUserBarrierUtil.CompareCanonical(barrier, envelope) > 0;
        if (candidateBecomesCanonical)
            DeletedUserBarrierUtil.ReplaceCanonical(barrier, envelope, payload, DateTimeOffset.UtcNow);

        barrier.HasConflict = true;
        barrier.ConflictingOperationId = candidateBecomesCanonical
            ? previousCanonicalOperationId
            : envelope.OperationId;
        barrier.ConflictingOperationHash = candidateBecomesCanonical
            ? previousCanonicalOperationHash
            : envelope.OperationHash.ToArray();
        barrier.LastUpdatedAtUtc = DateTimeOffset.UtcNow;
        _deletionBarriers.Update(barrier);
    }

    private async Task<UserControlOperationReceiptResult> TryApplyStoredSafelyAsync(
        Guid operationId,
        CancellationToken ct)
    {
        try
        {
            return await TryApplyStoredCoreAsync(operationId, ct);
        }
        catch
        {
            // Storage is committed before application begins. An application failure must not
            // leave partially mutated canonical/control entities tracked in this scoped context;
            // the durable StoredPending row remains discoverable for a later retry.
            _uow.ClearTrackedChanges();
            throw;
        }
    }

    private async Task<UserControlOperationReceiptResult> TryApplyStoredCoreAsync(
        Guid operationId,
        CancellationToken ct)
    {
        await using var transaction = await _uow.BeginTransactionAsync(ct);
        var row = await _operations.GetByIdAsync(operationId, ct)
            ?? throw new InvalidOperationException("The stored control operation does not exist.");
        var envelope = UserControlOperationEnvelopeUtil.Deserialize(row.EnvelopePayload);

        if (row.Status == UserControlOperationStatus.Applied)
        {
            await transaction.RollbackAsync(ct);
            return Receipt(envelope, UserControlOperationReceiptState.Applied, row.StatusReason);
        }
        if (row.Status == UserControlOperationStatus.Quarantined)
        {
            await transaction.RollbackAsync(ct);
            return Receipt(envelope, UserControlOperationReceiptState.Quarantined, row.StatusReason);
        }
        if (row.Status == UserControlOperationStatus.Rejected)
        {
            await transaction.RollbackAsync(ct);
            return Receipt(envelope, UserControlOperationReceiptState.Rejected, row.StatusReason);
        }

        if (envelope.OperationType == UserControlOperationType.AccountDeletion)
            return await ApplyAccountDeletionAsync(row, envelope, transaction, ct);

        if (_deletionBarriers is not null && await _deletionBarriers.ExistsAsync(row.UserId, ct))
        {
            row.Status = UserControlOperationStatus.Rejected;
            row.StatusReason = "The account identity is permanently deleted; this lifecycle operation is obsolete.";
            _operations.Update(row);
            await _uow.SaveChangesAsync(ct);
            await transaction.CommitAsync(ct);
            return Receipt(envelope, UserControlOperationReceiptState.Obsolete, row.StatusReason);
        }

        var user = await _users.GetByIdAsync(row.UserId, ct);
        if (user is null)
        {
            row.Status = UserControlOperationStatus.Rejected;
            row.StatusReason = "The canonical account no longer exists; the operation remains retained as evidence.";
            _operations.Update(row);
            await _uow.SaveChangesAsync(ct);
            await transaction.CommitAsync(ct);
            return Receipt(envelope, UserControlOperationReceiptState.Rejected, row.StatusReason);
        }

        var state = await GetOrCreateStateAsync(user!, ct);
        if (state.HasConflict)
        {
            row.Status = UserControlOperationStatus.Quarantined;
            row.StatusReason = state.ConflictReason ?? "The account control plane is quarantined.";
            _operations.Update(row);
            await _uow.SaveChangesAsync(ct);
            await transaction.CommitAsync(ct);
            return Receipt(envelope, UserControlOperationReceiptState.Quarantined, row.StatusReason);
        }

        if (envelope.OperationType == UserControlOperationType.DeviceAddition)
            return await ApplyDeviceAdditionAsync(row, envelope, user, state, transaction, ct);
        if (envelope.OperationType == UserControlOperationType.DeviceRemoval)
            return await ApplyDeviceRemovalAsync(row, envelope, user, state, transaction, ct);
        if (envelope.OperationType != UserControlOperationType.KeyEpochReplacement)
        {
            row.Status = UserControlOperationStatus.Rejected;
            row.StatusReason = "This control-operation type is explicitly unsupported.";
            _operations.Update(row);
            await _uow.SaveChangesAsync(ct);
            await transaction.CommitAsync(ct);
            return Receipt(envelope, UserControlOperationReceiptState.Rejected, row.StatusReason);
        }

        var sameBase = await _operations.ListKeyTransitionsFromAsync(envelope.UserId, envelope.PreviousKeyEpoch, ct);
        if (sameBase.Count(operation => operation.Status != UserControlOperationStatus.Rejected) > 1)
        {
            var reason = "Conflicting same-base key-epoch transitions are present; no winner is selected by arrival time.";
            foreach (var conflict in sameBase)
                Quarantine(conflict, envelope.OperationHash, reason);
            SetStateConflict(state, envelope.OperationId, envelope.OperationHash, reason);
            await _uow.SaveChangesAsync(ct);
            await transaction.CommitAsync(ct);
            return Receipt(envelope, UserControlOperationReceiptState.Quarantined, reason);
        }

        var payload = UserControlOperationEnvelopeUtil.DeserializeKeyEpochReplacementPayload(envelope.OperationPayload);
        if (payload.UserId != envelope.UserId ||
            payload.PreviousKeyEpoch != envelope.PreviousKeyEpoch ||
            payload.ResultingKeyEpoch != envelope.ResultingKeyEpoch ||
            payload.MembershipEpoch != envelope.ResultingMembershipEpoch)
        {
            row.Status = UserControlOperationStatus.Quarantined;
            row.StatusReason = "The replacement payload does not match the signed transition header.";
            _operations.Update(row);
            SetStateConflict(state, envelope.OperationId, envelope.OperationHash, row.StatusReason);
            await _uow.SaveChangesAsync(ct);
            await transaction.CommitAsync(ct);
            return Receipt(envelope, UserControlOperationReceiptState.Quarantined, row.StatusReason);
        }

        if (user.KeyEpoch == envelope.ResultingKeyEpoch)
        {
            if (!UserControlOperationEnvelopeUtil.MatchesKeyEpochReplacementPayload(payload, user))
            {
                row.Status = UserControlOperationStatus.Quarantined;
                row.StatusReason = "Canonical state is already at the resulting key epoch but does not match the signed replacement payload.";
                _operations.Update(row);
                SetStateConflict(state, envelope.OperationId, envelope.OperationHash, row.StatusReason);
                await _uow.SaveChangesAsync(ct);
                await transaction.CommitAsync(ct);
                return Receipt(envelope, UserControlOperationReceiptState.Quarantined, row.StatusReason);
            }

            row.Status = UserControlOperationStatus.Applied;
            row.AppliedAtUtc ??= DateTimeOffset.UtcNow;
            row.StatusReason = "The canonical account already matches the exact resulting key-epoch replacement.";
            state.AppliedKeyEpoch = Math.Max(state.AppliedKeyEpoch, envelope.ResultingKeyEpoch);
            state.AppliedMembershipEpoch = Math.Max(state.AppliedMembershipEpoch, envelope.ResultingMembershipEpoch);
            state.LastUpdatedAtUtc = DateTimeOffset.UtcNow;
            _operations.Update(row);
            if (_loginIdentities is not null)
                await _loginIdentities.RecalculateUnderLifecycleAsync(user.UId, ct);
            await _uow.SaveChangesAsync(ct);
            await transaction.CommitAsync(ct);
            _versionClock?.Observe([payload.GeneralUserDataVersion]);
            return Receipt(envelope, UserControlOperationReceiptState.Applied, row.StatusReason);
        }

        if (user.KeyEpoch > envelope.PreviousKeyEpoch)
        {
            row.Status = UserControlOperationStatus.Rejected;
            row.StatusReason = "The operation is obsolete because canonical state has already advanced beyond its base epoch.";
            _operations.Update(row);
            await _uow.SaveChangesAsync(ct);
            await transaction.CommitAsync(ct);
            return Receipt(envelope, UserControlOperationReceiptState.Obsolete, row.StatusReason);
        }
        if (user.KeyEpoch < envelope.PreviousKeyEpoch)
        {
            await transaction.RollbackAsync(ct);
            return Receipt(envelope, UserControlOperationReceiptState.StoredPending, "A preceding key-epoch operation is still missing.");
        }
        if (user.MembershipEpoch != envelope.PreviousMembershipEpoch)
        {
            row.Status = UserControlOperationStatus.Rejected;
            row.StatusReason = "The operation membership epoch is incompatible with canonical state.";
            _operations.Update(row);
            await _uow.SaveChangesAsync(ct);
            await transaction.CommitAsync(ct);
            return Receipt(envelope, UserControlOperationReceiptState.Rejected, row.StatusReason);
        }

        UserControlOperationEnvelopeUtil.ApplyKeyEpochReplacementPayload(payload, user);
        _users.Update(user);
        if (_canonicalHealth is not null)
            await _canonicalHealth.UpdateCheckpointAsync(user, ct);
        if (_syncFaults is not null)
        {
            // A locked installation can authenticate and apply the authority transition, but it
            // cannot prove the replacement bundle decrypts under the new user key. Preserve the
            // signed checkpoint while blocking publication until a keyed health gate verifies it.
            await _syncFaults.RecordAsync(new UserSyncFaultDescriptor
            {
                UserId = user.UId,
                Scope = UserSyncFaultScope.LocalCanonical,
                Kind = UserSyncFaultKind.CanonicalKeyVerificationPending,
                Status = UserSyncHealthStatus.AwaitingEvidence,
                AffectedComponent = "key-epoch-replacement",
                KeyEpoch = user.KeyEpoch,
                MembershipEpoch = user.MembershipEpoch,
                ExpectedHash = user.IntegrityHash,
                DiagnosticCode = "key-epoch-replacement-awaiting-keyed-verification",
                BlocksPublishing = true
            }, ct);
        }

        // Explicit old-epoch policy: remove verified, non-quarantined obsolete snapshots. Durable
        // fork/quarantine rows and the authoritative transition operation are retained.
        var obsoleteSnapshots = (await _snapshots.ListForUserAsync(user.UId, ct))
            .Where(snapshot =>
                snapshot.UserKeyEpoch <= envelope.PreviousKeyEpoch &&
                snapshot.Status != UserSyncSnapshotStatus.Quarantined)
            .ToList();
        if (obsoleteSnapshots.Count != 0)
            _snapshots.DeleteRange(obsoleteSnapshots);

        row.Status = UserControlOperationStatus.Applied;
        row.AppliedAtUtc = DateTimeOffset.UtcNow;
        row.StatusReason = null;
        _operations.Update(row);
        state.AppliedKeyEpoch = envelope.ResultingKeyEpoch;
        state.AppliedMembershipEpoch = envelope.ResultingMembershipEpoch;
        state.LastUpdatedAtUtc = DateTimeOffset.UtcNow;
        if (_loginIdentities is not null)
            await _loginIdentities.SetCanonicalAsync(user, user.GetGeneralUserDataVersion(), ct);

        await _uow.SaveChangesAsync(ct);
        await transaction.CommitAsync(ct);
        _versionClock?.Observe([payload.GeneralUserDataVersion]);

        // Session and Remember Me effects happen only after canonical replacement commits.
        await _interactiveSessions.LogoutUserAsync(
            user.UId,
            AuthSessionInvalidationReason.ProfilePasswordChanged,
            CancellationToken.None);
        return Receipt(envelope, UserControlOperationReceiptState.Applied);
    }

    private async Task<UserControlOperationReceiptResult> ApplyAccountDeletionAsync(
        UserControlOperation row,
        UserControlOperationEnvelope envelope,
        Abstractions.Persistence.IUnitOfWorkTransaction transaction,
        CancellationToken ct)
    {
        if (_deletionBarriers is null || _deletionCleanup is null)
            throw new InvalidOperationException("Authoritative account-deletion services are not registered.");

        var payload = UserControlOperationEnvelopeUtil.DeserializeAccountDeletionPayload(envelope.OperationPayload);
        DeletedUserBarrierUtil.ValidateEnvelopePayloadMatch(envelope, payload);

        var barrier = await _deletionBarriers.GetAsync(envelope.UserId, ct);
        string? resultDetail = null;
        if (barrier is null)
        {
            barrier = DeletedUserBarrierUtil.Create(envelope, payload, DateTimeOffset.UtcNow);
            await _deletionBarriers.AddAsync(barrier, ct);
        }
        else if (!DeletedUserBarrierUtil.Matches(barrier, envelope))
        {
            // Every independently valid deletion implies the same terminal state. Preserve the
            // non-canonical operation as explicit fork evidence and choose a deterministic canonical
            // barrier on every relay device.
            var previousCanonicalOperationId = barrier.DeletionOperationId;
            var previousCanonicalOperationHash = barrier.OperationHash.ToArray();
            var candidateBecomesCanonical = DeletedUserBarrierUtil.CompareCanonical(barrier, envelope) > 0;
            if (candidateBecomesCanonical)
                DeletedUserBarrierUtil.ReplaceCanonical(barrier, envelope, payload, DateTimeOffset.UtcNow);

            barrier.HasConflict = true;
            barrier.ConflictingOperationId = candidateBecomesCanonical
                ? previousCanonicalOperationId
                : envelope.OperationId;
            barrier.ConflictingOperationHash = candidateBecomesCanonical
                ? previousCanonicalOperationHash
                : envelope.OperationHash.ToArray();
            barrier.LastUpdatedAtUtc = DateTimeOffset.UtcNow;
            _deletionBarriers.Update(barrier);
            resultDetail = "A distinct valid deletion operation was retained as conflict evidence; deletion remains authoritative.";
        }

        await _deletionCleanup.DeleteCanonicalAndPendingStateAsync(envelope.UserId, ct);
        var state = await GetOrCreateStateAsync(envelope.UserId, envelope, ct);
        MarkApplied(row, state, envelope, resultDetail);
        await _uow.SaveChangesAsync(ct);
        await transaction.CommitAsync(ct);

        await _interactiveSessions.LogoutUserAsync(
            envelope.UserId,
            AuthSessionInvalidationReason.ProfileRemoved,
            CancellationToken.None);
        try
        {
            if (_enrollment is not null)
                await _enrollment.CancelEnrollmentAsync(CancellationToken.None);
            await _syncRuntime.RefreshSyncEnabledAsync(CancellationToken.None);
        }
        catch
        {
            // The durable operation remains relayable and retryable after a runtime wake-up failure.
        }
        return Receipt(envelope, UserControlOperationReceiptState.Applied, resultDetail);
    }

    private async Task<UserControlOperationReceiptResult> ApplyDeviceAdditionAsync(
        UserControlOperation row,
        UserControlOperationEnvelope envelope,
        User user,
        UserControlState state,
        Abstractions.Persistence.IUnitOfWorkTransaction transaction,
        CancellationToken ct)
    {
        var sameBase = await _operations.ListMembershipTransitionsFromAsync(envelope.UserId, envelope.PreviousMembershipEpoch, ct);
        if (sameBase.Count(operation => operation.Status != UserControlOperationStatus.Rejected) > 1)
            return await QuarantineMembershipConflictAsync(row, envelope, state, sameBase, transaction, ct);

        var payload = UserControlOperationEnvelopeUtil.DeserializeDeviceAdditionPayload(envelope.OperationPayload);
        if (payload.UserId != envelope.UserId || payload.KeyEpoch != envelope.PreviousKeyEpoch ||
            payload.PreviousMembershipEpoch != envelope.PreviousMembershipEpoch || payload.ResultingMembershipEpoch != envelope.ResultingMembershipEpoch ||
            envelope.PreviousKeyEpoch != envelope.ResultingKeyEpoch)
            return await QuarantinePayloadMismatchAsync(row, envelope, state, transaction, "The device-addition payload does not match the signed transition header.", ct);

        if (user.MembershipEpoch < envelope.PreviousMembershipEpoch)
        {
            await transaction.RollbackAsync(ct);
            return Receipt(envelope, UserControlOperationReceiptState.StoredPending, "A preceding membership operation is still missing.");
        }
        if (user.MembershipEpoch > envelope.PreviousMembershipEpoch)
        {
            var authorization = await _authorizationRows.GetActiveAsync(payload.UserId, payload.NewDeviceId, payload.NewOriginInstanceId, ct);
            if (authorization?.AdditionOperationId == envelope.OperationId && authorization.AdditionOperationHash is not null && HashEquals(authorization.AdditionOperationHash, envelope.OperationHash))
            {
                MarkApplied(row, state, envelope, "The exact device addition was already reflected in canonical membership.");
                if (_loginIdentities is not null)
                    await _loginIdentities.RecalculateUnderLifecycleAsync(user.UId, ct);
                await _uow.SaveChangesAsync(ct); await transaction.CommitAsync(ct);
                return Receipt(envelope, UserControlOperationReceiptState.Applied, row.StatusReason);
            }
            row.Status = UserControlOperationStatus.Rejected;
            row.StatusReason = "The device addition is obsolete because canonical membership advanced beyond its base epoch.";
            _operations.Update(row); await _uow.SaveChangesAsync(ct); await transaction.CommitAsync(ct);
            return Receipt(envelope, UserControlOperationReceiptState.Obsolete, row.StatusReason);
        }
        if (user.KeyEpoch != payload.KeyEpoch)
        {
            await transaction.RollbackAsync(ct);
            return Receipt(envelope, UserControlOperationReceiptState.StoredPending, "The matching key epoch is not available yet.");
        }

        try
        {
            await _membershipAuthorization.AuthorizeAdditionAsync(payload, envelope.OperationId, envelope.OperationHash, ct);
            await ApplyCurrentAdditionAsync(payload, ct);
        }
        catch (Exception ex) when (ex is InvalidDataException or InvalidOperationException or UnauthorizedAccessException)
        {
            return await QuarantinePayloadMismatchAsync(
                row,
                envelope,
                state,
                transaction,
                $"The device-addition transition is incompatible with retained authoritative identity state: {ex.Message}",
                ct);
        }
        user.MembershipEpoch = payload.ResultingMembershipEpoch;
        user.GenerateIntegrityHash();
        _users.Update(user);
        if (_canonicalHealth is not null)
            await _canonicalHealth.UpdateCheckpointAsync(user, ct);
        await RecordCanonicalVerificationPendingAsync(
            user,
            "membership-addition",
            "membership-addition-awaiting-keyed-verification",
            ct);
        MarkApplied(row, state, envelope, null);
        if (_loginIdentities is not null)
            await _loginIdentities.RecalculateUnderLifecycleAsync(user.UId, ct);
        await _uow.SaveChangesAsync(ct);
        await transaction.CommitAsync(ct);
        return Receipt(envelope, UserControlOperationReceiptState.Applied);
    }

    private async Task<UserControlOperationReceiptResult> ApplyDeviceRemovalAsync(
        UserControlOperation row,
        UserControlOperationEnvelope envelope,
        User user,
        UserControlState state,
        Abstractions.Persistence.IUnitOfWorkTransaction transaction,
        CancellationToken ct)
    {
        var sameBase = await _operations.ListMembershipTransitionsFromAsync(envelope.UserId, envelope.PreviousMembershipEpoch, ct);
        if (sameBase.Count(operation => operation.Status != UserControlOperationStatus.Rejected) > 1)
            return await QuarantineMembershipConflictAsync(row, envelope, state, sameBase, transaction, ct);

        var payload = UserControlOperationEnvelopeUtil.DeserializeDeviceRemovalPayload(envelope.OperationPayload);
        if (payload.UserId != envelope.UserId || payload.KeyEpoch != envelope.PreviousKeyEpoch ||
            payload.PreviousMembershipEpoch != envelope.PreviousMembershipEpoch || payload.ResultingMembershipEpoch != envelope.ResultingMembershipEpoch ||
            envelope.PreviousKeyEpoch != envelope.ResultingKeyEpoch)
            return await QuarantinePayloadMismatchAsync(row, envelope, state, transaction, "The device-removal payload does not match the signed transition header.", ct);

        if (user.MembershipEpoch < envelope.PreviousMembershipEpoch)
        {
            await transaction.RollbackAsync(ct);
            return Receipt(envelope, UserControlOperationReceiptState.StoredPending, "A preceding membership operation is still missing.");
        }
        if (user.MembershipEpoch > envelope.PreviousMembershipEpoch)
        {
            var ended = await _authorizationRows.ListForUserAsync(user.UId, ct);
            if (payload.Origins.All(origin => ended.Any(auth => auth.AuthorizationId == origin.AuthorizationId && auth.RemovalOperationId == envelope.OperationId && auth.RemovalOperationHash is not null && HashEquals(auth.RemovalOperationHash, envelope.OperationHash))))
            {
                MarkApplied(row, state, envelope, "The exact device removal was already reflected in canonical membership.");
                if (_loginIdentities is not null)
                    await _loginIdentities.RecalculateUnderLifecycleAsync(user.UId, ct);
                await _uow.SaveChangesAsync(ct); await transaction.CommitAsync(ct);
                return Receipt(envelope, UserControlOperationReceiptState.Applied, row.StatusReason);
            }
            row.Status = UserControlOperationStatus.Rejected;
            row.StatusReason = "The device removal is obsolete because canonical membership advanced beyond its base epoch.";
            _operations.Update(row); await _uow.SaveChangesAsync(ct); await transaction.CommitAsync(ct);
            return Receipt(envelope, UserControlOperationReceiptState.Obsolete, row.StatusReason);
        }

        var active = await _authorizationRows.ListActiveForDeviceAsync(user.UId, payload.RemovedDeviceId, ct);
        if (active.Count == 0 || active.Any(auth => payload.Origins.All(origin => origin.AuthorizationId != auth.AuthorizationId)))
            return await QuarantinePayloadMismatchAsync(row, envelope, state, transaction, "The removal does not exactly cover current installation authorizations.", ct);
        foreach (var authorization in active)
            await _membershipAuthorization.EndAuthorizationAsync(authorization, payload, envelope.OperationId, envelope.OperationHash, ct);

        var link = await _userDevices.GetAsync(user.UId, payload.RemovedDeviceId, ct);
        if (link is not null)
        {
            link.IsDeleted = true; link.DeletedAt = DateTimeOffset.UtcNow; link.IsSyncOn = false; link.LastModifiedAt = DateTimeOffset.UtcNow;
            _userDevices.Update(link);
        }
        var removesLocalInstallation = payload.RemovedDeviceId == _identity.LocalDeviceId && payload.Origins.Any(origin => origin.OriginInstanceId == _identity.OriginInstanceId);
        if (removesLocalInstallation)
        {
            user.SavedKey = null;
            var localLink = await _localUsers.GetAsync(user.UId, ct);
            if (localLink is not null) { localLink.IsSyncOn = false; _localUsers.Update(localLink); }
        }
        user.MembershipEpoch = payload.ResultingMembershipEpoch;
        user.GenerateIntegrityHash();
        _users.Update(user);
        if (_canonicalHealth is not null)
            await _canonicalHealth.UpdateCheckpointAsync(user, ct);
        await RecordCanonicalVerificationPendingAsync(
            user,
            "membership-removal",
            "membership-removal-awaiting-keyed-verification",
            ct);
        MarkApplied(row, state, envelope, null);
        if (_loginIdentities is not null)
            await _loginIdentities.RecalculateUnderLifecycleAsync(user.UId, ct);
        await _uow.SaveChangesAsync(ct);
        await transaction.CommitAsync(ct);
        if (removesLocalInstallation)
        {
            await _interactiveSessions.LogoutUserAsync(
                user.UId,
                AuthSessionInvalidationReason.ProfileRemoved,
                CancellationToken.None);
            await _syncRuntime.RefreshSyncEnabledAsync(CancellationToken.None);
        }
        return Receipt(envelope, UserControlOperationReceiptState.Applied);
    }

    private async Task ApplyCurrentAdditionAsync(DeviceAdditionPayload payload, CancellationToken ct)
    {
        var device = await _devices.GetByIdAsync(payload.NewDeviceId, ct);
        var isNew = device is null;
        if (device is not null &&
            (!device.PublicKey.SequenceEqual(payload.AgreementPublicKey) ||
             !device.SignPublicKey.SequenceEqual(payload.SignPublicKey) ||
             !string.Equals(SyncIdentityUtil.NormalizeFingerprint(device.TlsCertFingerprint), SyncIdentityUtil.NormalizeFingerprint(payload.TlsCertFingerprint), StringComparison.OrdinalIgnoreCase) ||
             device.DeviceType != payload.DeviceType) &&
            await _userDevices.HasAnyActiveLinkForDeviceAsync(payload.NewDeviceId, ct))
        {
            throw new InvalidDataException("The signed addition conflicts with an identity still used by an active membership link.");
        }
        device ??= new Device { Id = payload.NewDeviceId };
        device.PublicKey = payload.AgreementPublicKey.ToArray();
        device.SignPublicKey = payload.SignPublicKey.ToArray();
        device.TlsCertFingerprint = SyncIdentityUtil.NormalizeFingerprint(payload.TlsCertFingerprint);
        device.DeviceType = payload.DeviceType;
        device.IsTrusted = true; device.IsBlocked = false; device.BlockedReason = null; device.BlockedAt = null;
        device.LastSeen = DateTime.UtcNow; device.LastModifiedAt = DateTimeOffset.UtcNow; device.GenerateIntegrityHash();
        if (isNew) await _devices.AddAsync(device, ct); else _devices.Update(device);

        var link = await _userDevices.GetAsync(payload.UserId, payload.NewDeviceId, ct);
        if (link is null)
            await _userDevices.AddAsync(new UserDevice { UserId = payload.UserId, DeviceId = payload.NewDeviceId, IsSyncOn = true, IsDeleted = false, LastModifiedAt = DateTimeOffset.UtcNow }, ct);
        else
        {
            link.IsDeleted = false; link.DeletedAt = null; link.IsSyncOn = true; link.LastModifiedAt = DateTimeOffset.UtcNow;
            _userDevices.Update(link);
        }
    }

    private async Task<UserControlOperationReceiptResult> QuarantineMembershipConflictAsync(UserControlOperation row, UserControlOperationEnvelope envelope, UserControlState state, IReadOnlyList<UserControlOperation> sameBase, Abstractions.Persistence.IUnitOfWorkTransaction transaction, CancellationToken ct)
    {
        const string reason = "Conflicting same-base membership transitions are present; no winner is selected by arrival time.";
        foreach (var conflict in sameBase) Quarantine(conflict, envelope.OperationHash, reason);
        SetStateConflict(state, envelope.OperationId, envelope.OperationHash, reason);
        await _uow.SaveChangesAsync(ct); await transaction.CommitAsync(ct);
        return Receipt(envelope, UserControlOperationReceiptState.Quarantined, reason);
    }

    private async Task<UserControlOperationReceiptResult> QuarantinePayloadMismatchAsync(UserControlOperation row, UserControlOperationEnvelope envelope, UserControlState state, Abstractions.Persistence.IUnitOfWorkTransaction transaction, string reason, CancellationToken ct)
    {
        row.Status = UserControlOperationStatus.Quarantined; row.StatusReason = reason; _operations.Update(row);
        SetStateConflict(state, envelope.OperationId, envelope.OperationHash, reason);
        await _uow.SaveChangesAsync(ct); await transaction.CommitAsync(ct);
        return Receipt(envelope, UserControlOperationReceiptState.Quarantined, reason);
    }

    private void MarkApplied(UserControlOperation row, UserControlState state, UserControlOperationEnvelope envelope, string? reason)
    {
        row.Status = UserControlOperationStatus.Applied; row.AppliedAtUtc ??= DateTimeOffset.UtcNow; row.StatusReason = reason; _operations.Update(row);
        state.AppliedKeyEpoch = Math.Max(state.AppliedKeyEpoch, envelope.ResultingKeyEpoch);
        state.AppliedMembershipEpoch = Math.Max(state.AppliedMembershipEpoch, envelope.ResultingMembershipEpoch);
        state.LastUpdatedAtUtc = DateTimeOffset.UtcNow;
    }

    private async Task<UserControlState> GetOrCreateStateAsync(
        Guid userId,
        UserControlOperationEnvelope envelope,
        CancellationToken ct)
    {
        var state = await _states.GetAsync(userId, ct);
        if (state is not null)
            return state;

        state = new UserControlState
        {
            UserId = userId,
            LocalOriginInstanceId = Guid.Empty,
            NextOriginSequence = 1,
            AppliedKeyEpoch = envelope.ResultingKeyEpoch,
            AppliedMembershipEpoch = envelope.ResultingMembershipEpoch,
            LastUpdatedAtUtc = DateTimeOffset.UtcNow
        };
        await _states.AddAsync(state, ct);
        return state;
    }

    private async Task<UserControlState> GetOrCreateStateAsync(User user, CancellationToken ct)
    {
        var state = await _states.GetAsync(user.UId, ct);
        if (state is not null)
            return state;

        state = new UserControlState
        {
            UserId = user.UId,
            LocalOriginInstanceId = Guid.Empty,
            NextOriginSequence = 1,
            AppliedKeyEpoch = user.KeyEpoch,
            AppliedMembershipEpoch = user.MembershipEpoch,
            LastUpdatedAtUtc = DateTimeOffset.UtcNow
        };
        await _states.AddAsync(state, ct);
        return state;
    }

    private Task RecordCanonicalVerificationPendingAsync(
        User user,
        string affectedComponent,
        string diagnosticCode,
        CancellationToken ct)
    {
        if (_syncFaults is null)
            return Task.CompletedTask;

        // Membership operations can be authenticated while the installation is locked. The
        // checkpoint is updated transactionally, but publication remains blocked until a trusted
        // user key verifies the complete encrypted bundle and clears this scoped fault.
        return _syncFaults.RecordAsync(new UserSyncFaultDescriptor
        {
            UserId = user.UId,
            Scope = UserSyncFaultScope.LocalCanonical,
            Kind = UserSyncFaultKind.CanonicalKeyVerificationPending,
            Status = UserSyncHealthStatus.AwaitingEvidence,
            AffectedComponent = affectedComponent,
            KeyEpoch = user.KeyEpoch,
            MembershipEpoch = user.MembershipEpoch,
            ExpectedHash = user.IntegrityHash,
            DiagnosticCode = diagnosticCode,
            BlocksPublishing = true
        }, ct);
    }

    private async Task RecordControlFaultAsync(UserControlOperationEnvelope envelope, string? reason, CancellationToken ct)
    {
        if (_syncFaults is null)
            return;

        var kind = envelope.OperationType switch
        {
            UserControlOperationType.KeyEpochReplacement => UserSyncFaultKind.KeyEpochConflict,
            UserControlOperationType.DeviceAddition or UserControlOperationType.DeviceRemoval => UserSyncFaultKind.MembershipConflict,
            _ => UserSyncFaultKind.ControlOperationFork
        };
        await _syncFaults.RecordAsync(new UserSyncFaultDescriptor
        {
            UserId = envelope.UserId,
            Scope = UserSyncFaultScope.ControlPlane,
            Kind = kind,
            Status = UserSyncHealthStatus.TerminalConflict,
            AffectedComponent = envelope.OperationType.ToString(),
            OriginDeviceId = envelope.OriginDeviceId,
            OriginInstanceId = envelope.OriginInstanceId,
            KeyEpoch = envelope.PreviousKeyEpoch,
            MembershipEpoch = envelope.PreviousMembershipEpoch,
            OriginRevision = envelope.OriginSequence,
            ObservedHash = envelope.OperationHash.ToArray(),
            DiagnosticCode = string.IsNullOrWhiteSpace(reason) ? "control-conflict" : Truncate(reason),
            BlocksLifecycle = true
        }, ct);
        await _uow.SaveChangesAsync(ct);
    }

    private async Task MarkConflictAsync(
        UserControlOperation existing,
        Guid conflictingOperationId,
        byte[] conflictingHash,
        string reason,
        CancellationToken ct)
    {
        if (existing.OperationType == UserControlOperationType.AccountDeletion)
        {
            // Never disable application or relay of an already-verified deletion because a fork
            // later reused its identity/sequence. A StoredPending deletion must remain retryable;
            // an Applied deletion must remain relayable. Retain explicit evidence separately.
            existing.StatusReason = Truncate(reason);
            existing.ConflictingOperationHash = conflictingHash.ToArray();
            _operations.Update(existing);
            if (_deletionBarriers is not null)
            {
                var barrier = await _deletionBarriers.GetAsync(existing.UserId, ct);
                if (barrier is not null)
                {
                    barrier.HasConflict = true;
                    barrier.ConflictingOperationId = conflictingOperationId;
                    barrier.ConflictingOperationHash = conflictingHash.ToArray();
                    barrier.LastUpdatedAtUtc = DateTimeOffset.UtcNow;
                    _deletionBarriers.Update(barrier);
                }
            }
            return;
        }

        Quarantine(existing, conflictingHash, reason);
        var user = await _users.GetByIdAsync(existing.UserId, ct);
        var state = await _states.GetAsync(existing.UserId, ct);
        if (state is null)
        {
            state = new UserControlState
            {
                UserId = existing.UserId,
                LocalOriginInstanceId = Guid.Empty,
                NextOriginSequence = 1,
                AppliedKeyEpoch = user?.KeyEpoch ?? 0,
                AppliedMembershipEpoch = user?.MembershipEpoch ?? 0
            };
            await _states.AddAsync(state, ct);
        }
        SetStateConflict(state, conflictingOperationId, conflictingHash, reason);
    }

    private void Quarantine(UserControlOperation operation, byte[] conflictingHash, string reason)
    {
        operation.Status = UserControlOperationStatus.Quarantined;
        operation.StatusReason = Truncate(reason);
        operation.ConflictingOperationHash = conflictingHash.ToArray();
        _operations.Update(operation);
    }

    private void SetStateConflict(UserControlState state, Guid operationId, byte[] operationHash, string reason)
    {
        state.HasConflict = true;
        state.ConflictReason = Truncate(reason);
        state.ConflictingOperationId = operationId;
        state.ConflictingOperationHash = operationHash.ToArray();
        state.LastUpdatedAtUtc = DateTimeOffset.UtcNow;
    }


    private UserControlOperationReceiptState ToExistingReceiptState(UserControlOperationStatus status) =>
        status switch
        {
            UserControlOperationStatus.Applied => UserControlOperationReceiptState.Applied,
            UserControlOperationStatus.Quarantined => UserControlOperationReceiptState.Quarantined,
            UserControlOperationStatus.Rejected => UserControlOperationReceiptState.Rejected,
            _ => UserControlOperationReceiptState.AlreadyStored
        };

    private bool HashEquals(byte[] left, byte[] right) =>
        left.Length == right.Length && System.Security.Cryptography.CryptographicOperations.FixedTimeEquals(left, right);

    private string Truncate(string value) => value.Length <= 512 ? value : value[..512];

    private UserControlOperationReceiptResult Receipt(
        UserControlOperationEnvelope envelope,
        UserControlOperationReceiptState state,
        string? detail = null) =>
        new(
            envelope.OperationId,
            envelope.UserId,
            envelope.OriginDeviceId,
            envelope.OriginInstanceId,
            envelope.OriginSequence,
            envelope.OperationHash.ToArray(),
            state,
            detail);
}
