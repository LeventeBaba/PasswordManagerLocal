using PasswordManagerLocal.Common.Backend.Abstractions.Persistence;
using PasswordManagerLocal.Common.Backend.Abstractions.Repositories;
using PasswordManagerLocal.Common.Backend.Abstractions.Services;
using PasswordManagerLocal.Common.Backend.Exceptions;
using PasswordManagerLocal.Common.Backend.Models;
using PasswordManagerLocal.Common.Backend.Sync;

namespace PasswordManagerLocal.Common.Backend.Services;

/// <summary>
/// Orchestrates incoming and outgoing network deltas. Ordinary user-state updates are durably
/// stored as immutable pending snapshots and are never applied through blind encrypted replacement.
/// </summary>
public sealed class NetworkDeltaService : INetworkDeltaService
{
    private readonly IOutgoingDeltaBuilderService _outgoingDeltaBuilder;
    private readonly INetworkDeltaProtocolService _protocol;
    private readonly INetworkDeltaReplayService _replay;
    private readonly INetworkDeltaPayloadApplierService _payloadApplier;
    private readonly INetworkDeltaLifecycleService _lifecycle;
    private readonly IUserSnapshotInboxService _snapshotInbox;
    private readonly IUserSnapshotMergeCoordinator _mergeCoordinator;
    private readonly IUserSyncKeyResolverService _keyResolver;
    private readonly IUserRepository _users;
    private readonly IUserRevisionKnowledgeRepository _revisionKnowledge;
    private readonly IInteractiveSessionStateService _interactiveSessions;
    private readonly IUserControlOperationInboxService _controlOperationInbox;
    private readonly IDeviceIdentityService _identity;
    private readonly IUnitOfWork _uow;
    private readonly IUserTombstoneGarbageCollector? _garbageCollector;

    public NetworkDeltaService(
        IOutgoingDeltaBuilderService outgoingDeltaBuilder,
        INetworkDeltaProtocolService protocol,
        INetworkDeltaReplayService replay,
        INetworkDeltaPayloadApplierService payloadApplier,
        INetworkDeltaLifecycleService lifecycle,
        IDeviceIdentityService identity,
        IUnitOfWork uow,
        IUserSnapshotInboxService snapshotInbox,
        IUserSnapshotMergeCoordinator mergeCoordinator,
        IUserSyncKeyResolverService keyResolver,
        IUserRepository users,
        IUserRevisionKnowledgeRepository revisionKnowledge,
        IInteractiveSessionStateService interactiveSessions,
        IUserControlOperationInboxService controlOperationInbox,
        IUserTombstoneGarbageCollector? garbageCollector = null)
    {
        _outgoingDeltaBuilder = outgoingDeltaBuilder;
        _protocol = protocol;
        _replay = replay;
        _payloadApplier = payloadApplier;
        _lifecycle = lifecycle;
        _identity = identity;
        _uow = uow;
        _snapshotInbox = snapshotInbox;
        _mergeCoordinator = mergeCoordinator;
        _keyResolver = keyResolver;
        _users = users;
        _revisionKnowledge = revisionKnowledge;
        _interactiveSessions = interactiveSessions;
        _controlOperationInbox = controlOperationInbox;
        _garbageCollector = garbageCollector;
    }

    public Task<NetworkDelta> BuildAsync(SyncItem item, Device device, CancellationToken ct = default) =>
        _outgoingDeltaBuilder.BuildAsync(item, device, ct);

    public async Task<NetworkDeltaApplyResult> ApplyAsync(NetworkDelta delta, CancellationToken ct = default)
    {
        if (!_identity.IsSyncOn)
            throw new SyncRouteDisabledException("Local synchronization is disabled.");

        var validated = await _protocol.ValidateAndReadAsync(delta, ct);
        var sourceDevice = validated.SourceDevice;
        var payload = validated.Payload;

        await _protocol.ValidateSourceAuthorizationAsync(sourceDevice, payload, ct);

        if (payload.UserControlOperation is not null)
        {
            var receipt = await _controlOperationInbox.StoreAndApplyAsync(
                payload.UserControlOperation,
                sourceDevice.Id,
                ct);
            await _lifecycle.TouchSourceDeviceAsync(sourceDevice, ct);
            await _uow.SaveChangesAsync(ct);
            return new NetworkDeltaApplyResult(delta.Ts, null, receipt);
        }

        if (payload.ModelType == SyncModelType.User &&
            payload.ChangeType != SyncChangeType.Deleted)
        {
            return await StoreAndOptionallyMergeUserSnapshotAsync(payload, sourceDevice, delta.Ts, ct);
        }

        if (await _replay.IsBlockedByNewerTombstoneAsync(payload, delta.Ts, ct))
        {
            await _lifecycle.TouchSourceDeviceAsync(sourceDevice, ct);
            await _uow.SaveChangesAsync(ct);
            return new NetworkDeltaApplyResult(delta.Ts);
        }

        if (await _replay.IsAlreadyAppliedAsync(payload, delta.Ts, ct))
        {
            await _lifecycle.TouchSourceDeviceAsync(sourceDevice, ct);
            await _uow.SaveChangesAsync(ct);
            return new NetworkDeltaApplyResult(delta.Ts);
        }

        var applied = await _payloadApplier.ApplyAsync(payload, sourceDevice.Id, delta.Ts, ct);

        await _lifecycle.BeforeSaveAsync(payload, sourceDevice, applied, delta.Ts, ct);
        await _uow.SaveChangesAsync(ct);
        await _lifecycle.AfterSaveAsync(payload, sourceDevice, applied, delta.Ts, ct);

        return new NetworkDeltaApplyResult(delta.Ts);
    }

    private async Task<NetworkDeltaApplyResult> StoreAndOptionallyMergeUserSnapshotAsync(
        SyncDeltaPayload payload,
        Device sourceDevice,
        long transportTimestamp,
        CancellationToken ct)
    {
        var envelope = payload.UserSnapshot
            ?? throw new InvalidDataException("User snapshot envelope is missing.");

        var inbox = _snapshotInbox;
        var mergeCoordinator = _mergeCoordinator;
        var keyResolver = _keyResolver;
        var users = _users;

        var receipt = await inbox.StoreAsync(envelope, sourceDevice.Id, ct);
        await _lifecycle.TouchSourceDeviceAsync(sourceDevice, ct);
        await _uow.SaveChangesAsync(ct);

        var durable = receipt.State is
            UserSnapshotReceiptState.StoredPending or
            UserSnapshotReceiptState.ReplacedOlderPending or
            UserSnapshotReceiptState.AlreadyStored or
            UserSnapshotReceiptState.StoredMergedReceipt or
            UserSnapshotReceiptState.ObsoleteRevision or
            UserSnapshotReceiptState.RejectedAccountDeleted;
        if (!durable)
            return new NetworkDeltaApplyResult(transportTimestamp, receipt);

        var user = await users.GetByIdAsync(envelope.UserId, ct);
        if (user is null || !keyResolver.TryResolve(user, out var key) || key is null)
            return new NetworkDeltaApplyResult(transportTimestamp, receipt);

        using (key)
        {
            if (!await mergeCoordinator.TryMergePendingAsync(envelope.UserId, key, ct))
            {
                if (receipt.State == UserSnapshotReceiptState.StoredMergedReceipt && _garbageCollector is not null)
                {
                    try
                    {
                        await _garbageCollector.CollectAsync(envelope.UserId, key, ct);
                    }
                    catch (Exception ex) when (ex is not OperationCanceledException)
                    {
                        // The authenticated receipt is already durable. Conservative cleanup can
                        // be retried during login, Remember Me unlock, enrollment, or maintenance.
                    }
                }

                return new NetworkDeltaApplyResult(transportTimestamp, receipt);
            }
        }

        // A batch merge can succeed because of a different origin while this exact candidate
        // was quarantined. Report MergedImmediately only when durable knowledge proves that the
        // acknowledged origin revision is actually covered by canonical state.
        {
            var knowledge = await _revisionKnowledge.GetAsync(
                envelope.UserId,
                envelope.OriginDeviceId,
                envelope.OriginInstanceId,
                envelope.UserKeyEpoch,
                ct);
            if (knowledge is null || knowledge.HighestMergedRevision < envelope.OriginRevision)
                return new NetworkDeltaApplyResult(transportTimestamp, receipt);
        }

        var refreshedUser = await users.GetByIdWithRelationsAsync(envelope.UserId, ct);
        if (refreshedUser is not null)
            await _interactiveSessions.RefreshSyncedUserSessionsAsync(refreshedUser, ct);

        var mergedReceipt = receipt with
        {
            State = UserSnapshotReceiptState.MergedImmediately,
            Detail = "The pending snapshot was durably stored and then incorporated into canonical state."
        };
        return new NetworkDeltaApplyResult(transportTimestamp, mergedReceipt);
    }
}
