using PasswordManagerLocal.Common.Backend.Abstractions.Persistence;
using PasswordManagerLocal.Common.Backend.Abstractions.Repositories;
using PasswordManagerLocal.Common.Backend.Abstractions.Services;
using PasswordManagerLocal.Common.Backend.Constants;
using PasswordManagerLocal.Common.Backend.Diagnostics;
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
                var evidenceRepairs = new List<(UserMembershipAuthorization Authorization, Guid? AdditionId, byte[]? AdditionHash)>();
                var allKnownMerged = true;
                foreach (var authorization in activeAuthorizations)
                {
                    var additionId = authorization.AdditionOperationId;
                    var additionHash = authorization.AdditionOperationHash;
                    if (additionId is null && additionHash is { Length: 0 })
                    {
                        if (authorization.IsGenesis && authorization.StartedMembershipEpoch == 1)
                        {
                            // Genesis has no addition operation. Older rows can encode its absent hash as an empty blob.
                            additionHash = null;
                        }
                        else if (!authorization.IsGenesis)
                        {
                            (additionId, additionHash) = await RecoverSignedAdditionEvidenceAsync(authorization, lifecycleToken);
                        }
                        else
                        {
                            throw new InvalidDataException("The genesis authorization has an invalid membership epoch.");
                        }

                        evidenceRepairs.Add((authorization, additionId, additionHash));
                    }

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
                            AuthorizationId = UserMembershipAuthorizationIdentity.GetCanonicalAuthorizationId(authorization),
                            OriginInstanceId = authorization.OriginInstanceId,
                            UserKeyEpoch = keyEpoch,
                            HighestAcceptedSnapshotRevision = item?.HighestStoredRevision ?? 0,
                            HighestAcceptedControlSequence = highestControlSequence,
                            SignPublicKeyHash = authorization.SignPublicKeyHash.ToArray(),
                            AdditionOperationId = additionId,
                            AdditionOperationHash = additionHash?.ToArray()
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
                try
                {
                    UserControlOperationEnvelopeUtil.FinalizeDeviceRemovalPayload(payload);
                }
                catch (InvalidDataException ex)
                {
                    BackendDebugLog.Error(
                        $"Device removal payload validation failed. UserId={user.UId}, RemovedDeviceId={deviceId}, " +
                        $"KeyEpoch={user.KeyEpoch}, PreviousMembershipEpoch={user.MembershipEpoch}, " +
                        $"ActiveAuthorizationCount={activeAuthorizations.Count}, CutoffCount={cutoffRows.Count}, " +
                        $"CutoffProblems={DescribeRemovalCutoffProblems(cutoffRows)}",
                        ex,
                        "DeviceRemoval");
                    throw;
                }

                // The durable version clock writes through a separate DbContext. Reserve the
                // tombstone version before this SQLite transaction acquires the write lock.
                var encryptedDeviceRemovalVersion = _versionClock.Next();

                await using var transaction = await _uow.BeginTransactionAsync(lifecycleToken);
                try
                {
                    foreach (var repair in evidenceRepairs)
                    {
                        repair.Authorization.AdditionOperationId = repair.AdditionId;
                        repair.Authorization.AdditionOperationHash = repair.AdditionHash?.ToArray();
                        repair.Authorization.Version = checked(repair.Authorization.Version + 1);
                        _membershipAuthorizations.Update(repair.Authorization);
                    }

                    using var bundle = await _userDataReader.GetLoadAndVerifyUserDataBundleAsync(token, lifecycleToken, user);
                    var now = DateTimeOffset.UtcNow;
                    var encryptedDevice = bundle.UserDevicesData.Devices.FirstOrDefault(item => item.Id == deviceId);
                    // Membership can arrive before its encrypted presentation metadata. The
                    // removal must still dominate a late snapshot containing that metadata.
                    TombstoneCleanupUtil.AddOrUpdateDeletedUserDevice(bundle.UserDevicesData, deviceId, now, encryptedDeviceRemovalVersion);
                    if (encryptedDevice is not null)
                    {
                        encryptedDevice.Dispose();
                        bundle.UserDevicesData.Devices.Remove(encryptedDevice);
                    }
                    await _userDataWriter.UpdateUserDataBundleAsync(bundle, token, UserDataBlobKind.Devices, false, lifecycleToken);
                    user = await _userLookup.GetAndVerifyUserAsync(token, lifecycleToken);

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
                    foreach (var repair in evidenceRepairs)
                        BackendDebugLog.Info(
                            $"Recovered membership addition evidence during committed device removal. " +
                            $"UserId={user.UId}, RemovedDeviceId={deviceId}, AuthorizationId={repair.Authorization.AuthorizationId}, " +
                            $"Genesis={repair.Authorization.IsGenesis}, SignedAdditionRecovered={repair.AdditionId.HasValue}.",
                            "DeviceRemoval");
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

    private async Task<(Guid AdditionId, byte[] AdditionHash)> RecoverSignedAdditionEvidenceAsync(
        UserMembershipAuthorization authorization, CancellationToken ct)
    {
        if (authorization.IsGenesis || authorization.StartedMembershipEpoch <= 1)
            throw new InvalidDataException("The non-genesis authorization has no valid addition epoch.");

        var transitions = await _controlOperations.ListMembershipTransitionsFromAsync(
            authorization.UserId, authorization.StartedMembershipEpoch - 1, ct);
        var matches = new List<UserControlOperationEnvelope>();
        foreach (var operation in transitions.Where(row =>
                     row.Status == UserControlOperationStatus.Applied &&
                     row.OperationType == UserControlOperationType.DeviceAddition &&
                     row.ResultingMembershipEpoch == authorization.StartedMembershipEpoch))
        {
            var envelope = UserControlOperationEnvelopeUtil.Deserialize(operation.EnvelopePayload);
            if (operation.OperationId != envelope.OperationId || operation.UserId != envelope.UserId ||
                envelope.UserId != authorization.UserId || envelope.OperationType != UserControlOperationType.DeviceAddition ||
                operation.OriginDeviceId != envelope.OriginDeviceId || operation.OriginInstanceId != envelope.OriginInstanceId ||
                operation.PreviousKeyEpoch != envelope.PreviousKeyEpoch ||
                operation.PreviousMembershipEpoch != envelope.PreviousMembershipEpoch ||
                envelope.PreviousMembershipEpoch != authorization.StartedMembershipEpoch - 1 ||
                operation.ResultingMembershipEpoch != envelope.ResultingMembershipEpoch ||
                !Hashing.Verify(operation.OperationHash, envelope.OperationHash))
                throw new InvalidDataException("An applied addition operation disagrees with its signed envelope.");

            var author = await _membershipAuthorizations.GetForSignedEpochAsync(
                authorization.UserId, envelope.OriginDeviceId, envelope.OriginInstanceId,
                envelope.PreviousMembershipEpoch, ct);
            if (author is null || envelope.PreviousKeyEpoch < author.MinimumKeyEpoch ||
                (author.MaximumKeyEpoch is long maximum && envelope.PreviousKeyEpoch > maximum))
                throw new InvalidDataException("The applied addition operation has no authorized historical signer.");
            UserControlOperationEnvelopeUtil.VerifyWithSigningKey(envelope, author.SignPublicKey);

            var payload = UserControlOperationEnvelopeUtil.DeserializeDeviceAdditionPayload(envelope.OperationPayload);
            if (payload.UserId == authorization.UserId && payload.NewDeviceId == authorization.DeviceId &&
                payload.NewOriginInstanceId == authorization.OriginInstanceId &&
                payload.ResultingMembershipEpoch == authorization.StartedMembershipEpoch &&
                payload.PreviousMembershipEpoch == envelope.PreviousMembershipEpoch &&
                payload.KeyEpoch == authorization.MinimumKeyEpoch &&
                payload.KeyEpoch == envelope.PreviousKeyEpoch && envelope.ResultingKeyEpoch == envelope.PreviousKeyEpoch &&
                payload.SignPublicKey.SequenceEqual(authorization.SignPublicKey) &&
                Hashing.Verify(authorization.SignPublicKeyHash, Hashing.SHA256Hash(payload.SignPublicKey)) &&
                Hashing.Verify(authorization.AgreementPublicKeyHash, Hashing.SHA256Hash(payload.AgreementPublicKey)) &&
                string.Equals(SyncIdentityUtil.NormalizeFingerprint(payload.TlsCertFingerprint),
                    SyncIdentityUtil.NormalizeFingerprint(authorization.TlsCertFingerprint), StringComparison.Ordinal) &&
                payload.DeviceType == authorization.DeviceType)
                matches.Add(envelope);
        }

        if (matches.Count != 1)
            throw new InvalidDataException(
                $"Cannot recover addition evidence for authorization {authorization.AuthorizationId}: " +
                $"expected one matching applied signed operation, found {matches.Count}. No membership data was repaired.");
        return (matches[0].OperationId, matches[0].OperationHash.ToArray());
    }

    private static string DescribeRemovalCutoffProblems(IReadOnlyList<DeviceRemovalOriginCutoffPayload> cutoffs)
    {
        var problems = new List<string>();
        var seen = new HashSet<(Guid AuthorizationId, long KeyEpoch)>();
        for (var index = 0; index < cutoffs.Count; index++)
        {
            var cutoff = cutoffs[index];
            var fields = new List<string>();
            if (cutoff.AuthorizationId == Guid.Empty) fields.Add("AuthorizationId is empty");
            if (cutoff.OriginInstanceId == Guid.Empty) fields.Add("OriginInstanceId is empty");
            if (cutoff.UserKeyEpoch <= 0) fields.Add($"UserKeyEpoch={cutoff.UserKeyEpoch}");
            if (cutoff.HighestAcceptedSnapshotRevision < 0)
                fields.Add($"HighestAcceptedSnapshotRevision={cutoff.HighestAcceptedSnapshotRevision}");
            if (cutoff.HighestAcceptedControlSequence < 0)
                fields.Add($"HighestAcceptedControlSequence={cutoff.HighestAcceptedControlSequence}");
            if (cutoff.SignPublicKeyHash.Length != SyncConstants.SyncDeltaPayloadHashBytes)
                fields.Add($"SignPublicKeyHashLength={cutoff.SignPublicKeyHash.Length}, expected={SyncConstants.SyncDeltaPayloadHashBytes}");
            if ((cutoff.AdditionOperationId is null) != (cutoff.AdditionOperationHash is null))
                fields.Add($"AdditionOperationIdPresent={cutoff.AdditionOperationId is not null}, " +
                           $"AdditionOperationHashPresent={cutoff.AdditionOperationHash is not null}");
            if (cutoff.AdditionOperationHash is not null && cutoff.AdditionOperationHash.Length != SyncConstants.SyncDeltaPayloadHashBytes)
                fields.Add($"AdditionOperationHashLength={cutoff.AdditionOperationHash.Length}, expected={SyncConstants.SyncDeltaPayloadHashBytes}");
            if (!seen.Add((cutoff.AuthorizationId, cutoff.UserKeyEpoch)))
                fields.Add("duplicate AuthorizationId and UserKeyEpoch");

            if (fields.Count > 0)
                problems.Add($"row={index}, AuthorizationId={cutoff.AuthorizationId}, " +
                             $"OriginInstanceId={cutoff.OriginInstanceId}, KeyEpoch={cutoff.UserKeyEpoch}: " +
                             string.Join(", ", fields));
        }

        return problems.Count == 0 ? "no invalid origin field found" : string.Join("; ", problems);
    }

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
