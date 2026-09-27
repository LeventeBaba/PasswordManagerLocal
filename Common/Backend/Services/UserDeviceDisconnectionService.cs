using PasswordManagerLocal.Common.Backend.Abstractions.Persistence;
using PasswordManagerLocal.Common.Backend.Abstractions.Repositories;
using PasswordManagerLocal.Common.Backend.Abstractions.Services;
using PasswordManagerLocal.Common.Backend.Exceptions;
using PasswordManagerLocal.Common.Backend.Internal.Devices;
using PasswordManagerLocal.Common.Backend.Models;
using PasswordManagerLocal.Common.Backend.Models.Encrypted;
using PasswordManagerLocal.Common.Contracts.Responses;
using PasswordManagerLocal.Common.Backend.Security;
using PasswordManagerLocal.Common.Backend.Sync;
using PasswordManagerLocal.Common.Backend.Utils;
using static PasswordManagerLocal.Common.Contracts.Validation.DataValidation;
using PasswordManagerLocal.Common.Backend.Sync.Tombstones;

namespace PasswordManagerLocal.Common.Backend.Services;

public sealed class UserDeviceDisconnectionService : IUserDeviceDisconnectionService
{
    private readonly IUserLookupService _userLookup;
    private readonly IUserDataReaderService _userDataReader;
    private readonly IUserDataWriterService _userDataWriter;
    private readonly ICredentialVerificationService _credentialVerification;
    private readonly IDeviceIdentityService _identity;
    private readonly IGroupRepository _groups;
    private readonly IUserDeviceRepository _userDevices;
    private readonly ISyncRouteRepository _syncRoutes;
    private readonly ISyncQueueRepository _syncQueueItems;
    private readonly IUserLifecycleCoordinator _lifecycle;
    private readonly IUserSnapshotMergeCoordinator _snapshotMerge;
    private readonly IUserMembershipAuthorizationRepository _membershipAuthorizations;
    private readonly IUserRevisionKnowledgeRepository _revisionKnowledge;
    private readonly IUserControlOperationRepository _controlOperations;
    private readonly IUserControlStateRepository _controlStates;
    private readonly IUserControlOperationWriterService _controlWriter;
    private readonly IUserSnapshotPublisherService _snapshotPublisher;
    private readonly ISyncQueueWriterService _queueWriter;
    private readonly IPendingSyncActivationService _activation;
    private readonly IKeyVaultService _keyVault;
    private readonly IUserSyncSnapshotRepository _snapshots;
    private readonly ISyncVersionClockService _versionClock;
    private readonly IUnitOfWork _uow;
    private readonly IUserTombstoneGarbageCollector? _garbageCollector;
    private readonly UserDeviceAccessor _accessor;

    public UserDeviceDisconnectionService(
        IUserLookupService userLookup,
        IUserDataReaderService userDataReader,
        IUserDataWriterService userDataWriter,
        ICredentialVerificationService credentialVerification,
        IDeviceIdentityService identity,
        IGroupRepository groups,
        IUserDeviceRepository userDevices,
        ISyncRouteRepository syncRoutes,
        ISyncQueueRepository syncQueueItems,
        IUserLifecycleCoordinator lifecycle,
        IUserSnapshotMergeCoordinator snapshotMerge,
        IUserMembershipAuthorizationRepository membershipAuthorizations,
        IUserRevisionKnowledgeRepository revisionKnowledge,
        IUserControlOperationRepository controlOperations,
        IUserControlStateRepository controlStates,
        IUserControlOperationWriterService controlWriter,
        IUserSnapshotPublisherService snapshotPublisher,
        ISyncQueueWriterService queueWriter,
        IPendingSyncActivationService activation,
        IKeyVaultService keyVault,
        IUserSyncSnapshotRepository snapshots,
        ISyncVersionClockService versionClock,
        IUnitOfWork uow,
        UserDeviceAccessor accessor,
        IUserTombstoneGarbageCollector? garbageCollector = null)
    {
        _userLookup = userLookup;
        _userDataReader = userDataReader;
        _userDataWriter = userDataWriter;
        _credentialVerification = credentialVerification;
        _identity = identity;
        _groups = groups;
        _userDevices = userDevices;
        _syncRoutes = syncRoutes;
        _syncQueueItems = syncQueueItems;
        _lifecycle = lifecycle;
        _snapshotMerge = snapshotMerge;
        _membershipAuthorizations = membershipAuthorizations;
        _revisionKnowledge = revisionKnowledge;
        _controlOperations = controlOperations;
        _controlStates = controlStates;
        _controlWriter = controlWriter;
        _snapshotPublisher = snapshotPublisher;
        _queueWriter = queueWriter;
        _activation = activation;
        _keyVault = keyVault;
        _snapshots = snapshots;
        _versionClock = versionClock;
        _uow = uow;
        _accessor = accessor;
        _garbageCollector = garbageCollector;
    }

    public Task<DeviceRemovalResultResponse> DisconnectUserDeviceAsync(Guid token, Guid deviceId, byte[] masterPassword, CancellationToken ct = default)
    {
        if (!IsValidPassword(masterPassword) || deviceId == _identity.LocalDeviceId)
            throw new InvalidInputException();
        if (!_keyVault.TryGetEncryptionKey(token, out var key))
            throw new InvalidTokenException();
        return RemoveUnderLifecycleAsync(token, deviceId, masterPassword, key, ct);
    }

    private async Task<DeviceRemovalResultResponse> RemoveUnderLifecycleAsync(Guid token, Guid deviceId, byte[] masterPassword, PasswordManagerLocal.Common.Backend.Security.EncryptionKey key, CancellationToken ct)
    {
        using (key)
        {
            var userId = (await _userLookup.GetAndVerifyUserAsync(token, ct)).UId;
            return await _lifecycle.ExecuteAsync(userId, async lifecycleToken =>
            {
                var user = await _userLookup.GetAndVerifyUserAsync(token, lifecycleToken);
                if (!_credentialVerification.IsPasswordValid(token, masterPassword, user.PasswordSalt))
                    throw new InvalidInputException();
                await _accessor.GetActiveRemoteAsync(user.UId, deviceId, lifecycleToken);

                var controlState = await _controlStates.GetAsync(user.UId, lifecycleToken);
                if (controlState?.HasConflict == true)
                    throw new InvalidOperationException(controlState.ConflictReason ?? "The account control plane is quarantined.");
                if (await HasQuarantinedSnapshotsAsync(user, lifecycleToken))
                    throw new InvalidOperationException("Device removal is blocked by unresolved snapshot fork evidence.");

                await _snapshotMerge.TryMergePendingUnderLifecycleAsync(user.UId, key, UserSyncKeyConfidence.AuthenticatedSession, lifecycleToken);
                user = await _userLookup.GetAndVerifyUserAsync(token, lifecycleToken);
                var activeAuthorizations = await _membershipAuthorizations.ListActiveForDeviceAsync(user.UId, deviceId, lifecycleToken);
                if (activeAuthorizations.Count == 0)
                    throw new InvalidInputException();

                var cutoffRows = new List<DeviceRemovalOriginCutoffPayload>();
                var allKnownMerged = true;
                foreach (var authorization in activeAuthorizations)
                {
                    var knowledge = await _revisionKnowledge.ListForOriginAsync(user.UId, deviceId, authorization.OriginInstanceId, lifecycleToken);
                    var knowledgeByEpoch = knowledge.ToDictionary(item => item.UserKeyEpoch);
                    var highestControlSequence = await _controlOperations.GetHighestOriginSequenceAsync(user.UId, deviceId, authorization.OriginInstanceId, lifecycleToken);
                    var maximumKeyEpoch = authorization.MaximumKeyEpoch ?? user.KeyEpoch;
                    if (maximumKeyEpoch < authorization.MinimumKeyEpoch || maximumKeyEpoch > user.KeyEpoch)
                        throw new InvalidOperationException("The installation authorization key-epoch range is invalid.");

                    for (var keyEpoch = authorization.MinimumKeyEpoch; keyEpoch <= maximumKeyEpoch; keyEpoch = checked(keyEpoch + 1))
                    {
                        knowledgeByEpoch.TryGetValue(keyEpoch, out var item);
                        if (item is not null)
                            allKnownMerged &= item.HighestMergedRevision >= item.HighestStoredRevision;
                        cutoffRows.Add(new DeviceRemovalOriginCutoffPayload
                        {
                            AuthorizationId = authorization.AuthorizationId,
                            OriginInstanceId = authorization.OriginInstanceId,
                            UserKeyEpoch = keyEpoch,
                            HighestAcceptedSnapshotRevision = item?.HighestStoredRevision ?? 0,
                            HighestAcceptedControlSequence = highestControlSequence,
                            SignPublicKeyHash = authorization.SignPublicKeyHash.ToArray(),
                            AdditionOperationId = authorization.AdditionOperationId,
                            AdditionOperationHash = authorization.AdditionOperationHash?.ToArray()
                        });
                        if (keyEpoch == long.MaxValue)
                            break;
                    }
                }

                var payload = new DeviceRemovalPayload
                {
                    UserId = user.UId,
                    RemovedDeviceId = deviceId,
                    PreviousMembershipEpoch = user.MembershipEpoch,
                    ResultingMembershipEpoch = checked(user.MembershipEpoch + 1),
                    KeyEpoch = user.KeyEpoch,
                    Origins = cutoffRows
                };
                UserControlOperationEnvelopeUtil.FinalizeDeviceRemovalPayload(payload);

                // The durable version clock writes through a separate DbContext. Reserve the
                // tombstone version before this SQLite transaction acquires the write lock.
                var encryptedDeviceRemovalVersion = _versionClock.Next();

                await using var transaction = await _uow.BeginTransactionAsync(lifecycleToken);
                try
                {
                    var bundle = await _userDataReader.GetLoadAndVerifyUserDataBundleAsync(token, lifecycleToken, user);
                    var now = DateTimeOffset.UtcNow;
                    var encryptedDevice = bundle.UserDevicesData.Devices.FirstOrDefault(item => item.Id == deviceId);
                    if (encryptedDevice is not null)
                    {
                        TombstoneCleanupUtil.AddOrUpdateDeletedUserDevice(bundle.UserDevicesData, encryptedDevice.Id, now, encryptedDeviceRemovalVersion);
                        encryptedDevice.Dispose();
                        bundle.UserDevicesData.Devices.Remove(encryptedDevice);
                        await _userDataWriter.UpdateUserDataBundleAsync(bundle, token, UserDataBlobKind.Devices, false, lifecycleToken);
                        user = await _userLookup.GetAndVerifyUserAsync(token, lifecycleToken);
                    }

                    var envelope = await _controlWriter.CreateAppliedDeviceRemovalUnderLifecycleAsync(user, payload, lifecycleToken);
                    user = await _userLookup.GetAndVerifyUserAsync(token, lifecycleToken);
                    var localSnapshot = await _snapshotPublisher.GetOrCreateAsync(user, lifecycleToken);
                    await _queueWriter.EnqueueAsync(new SyncItem
                    {
                        ModelId = user.UId,
                        ModelType = SyncModelType.User,
                        ChangeType = SyncChangeType.Updated,
                        ChangedAtTs = localSnapshot.CreatedAtUtc.ToUnixTimeMilliseconds()
                    }, localSnapshot.CreatedAtUtc.ToUnixTimeMilliseconds(), [deviceId], true, false, lifecycleToken);
                    await RemovePendingSyncsForUserToDeviceAsync(user.UId, deviceId, lifecycleToken);
                    await _uow.SaveChangesAsync(lifecycleToken);
                    await transaction.CommitAsync(lifecycleToken);
                    if (_garbageCollector is not null)
                    {
                        try { await _garbageCollector.CollectAsync(user.UId, key, CancellationToken.None); } catch { }
                    }
                    try { await _activation.ActivatePendingAsync(CancellationToken.None); } catch { }
                    return new DeviceRemovalResultResponse
                    {
                        Removed = true,
                        AllKnownOriginRevisionsMerged = allKnownMerged,
                        MayContainUnobservedChanges = true,
                        Message = allKnownMerged
                            ? "Removal completed. All revisions known to this installation were merged, but the removed device may still contain changes that were never observed here."
                            : "Removal completed with a signed cutoff, but some known origin revisions were not proven merged and the removed device may contain unseen changes.",
                        OperationId = envelope.OperationId,
                        ResultingMembershipEpoch = envelope.ResultingMembershipEpoch
                    };
                }
                catch
                {
                    await transaction.RollbackAsync(CancellationToken.None);
                    _uow.ClearTrackedChanges();
                    throw;
                }
            }, ct);
        }
    }

    private async Task<bool> HasQuarantinedSnapshotsAsync(User user, CancellationToken ct) =>
        (await _snapshots.ListForUserAsync(user.UId, ct))
            .Any(snapshot => snapshot.UserKeyEpoch == user.KeyEpoch && snapshot.Status == UserSyncSnapshotStatus.Quarantined);

    private async Task RemovePendingSyncsForUserToDeviceAsync(Guid userId, Guid targetDeviceId, CancellationToken ct)
    {
        var pendingItems = await _syncQueueItems.ListPendingForDeviceWithItemsAsync(targetDeviceId, ct);
        foreach (var queueItem in pendingItems)
        {
            if (queueItem.SyncItem is not null && await IsSyncItemOnlyForRemovedUserOrRouteAsync(queueItem.SyncItem, userId, targetDeviceId, ct))
                _syncQueueItems.Delete(queueItem);
        }
    }

    private async Task<bool> IsSyncItemOnlyForRemovedUserOrRouteAsync(SyncItem item, Guid removedUserId, Guid targetDeviceId, CancellationToken ct)
    {
        if (item.ModelType == SyncModelType.User)
            return item.ModelId == removedUserId;

        if (item.ModelType == SyncModelType.UserDevice)
        {
            var link = await _userDevices.GetByModelIdAsync(item.ModelId, ct);
            return link?.UserId == removedUserId;
        }

        if (item.ModelType == SyncModelType.Group)
        {
            var userIds = await _groups.ListUserIdsAsync(item.ModelId, ct);
            if (!userIds.Contains(removedUserId))
                return false;

            return !await AnyOtherUserCanStillSyncToTargetAsync(userIds, removedUserId, targetDeviceId, ct);
        }

        if (item.ModelType == SyncModelType.Device)
        {
            var links = await _userDevices.ListByDeviceAsync(item.ModelId, ct);
            if (links.All(link => link.UserId != removedUserId))
                return false;

            return !await AnyOtherUserCanStillSyncToTargetAsync(
                links.Where(link => !link.IsDeleted && link.IsSyncOn).Select(link => link.UserId),
                removedUserId,
                targetDeviceId,
                ct);
        }

        return false;
    }

    private Task<bool> AnyOtherUserCanStillSyncToTargetAsync(
        IEnumerable<Guid> userIds,
        Guid removedUserId,
        Guid targetDeviceId,
        CancellationToken ct)
    {
        var candidateUserIds = userIds
            .Where(id => id != Guid.Empty && id != removedUserId)
            .Distinct()
            .ToArray();
        return _syncRoutes.HasAnyEligibleAsync(candidateUserIds, targetDeviceId, ct);
    }
}
