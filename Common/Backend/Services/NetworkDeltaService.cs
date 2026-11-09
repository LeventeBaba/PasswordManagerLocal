using Microsoft.Extensions.DependencyInjection;
using PasswordManagerLocal.Common.Backend.Abstractions.Persistence;
using PasswordManagerLocal.Common.Backend.Abstractions.Repositories;
using PasswordManagerLocal.Common.Backend.Abstractions.Services;
using PasswordManagerLocal.Common.Backend.Exceptions;
using PasswordManagerLocal.Common.Backend.Models;
using PasswordManagerLocal.Common.Backend.Persistence;
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
    private readonly IServiceScopeFactory? _scopes;
    private readonly IDeviceIdentityService _identity;
    private readonly IUnitOfWork _uow;
    private readonly IUserTombstoneGarbageCollector? _garbageCollector;

    private readonly IUserPostLoginWorkScheduler? _postLogin;
    private readonly IUserLifecycleCoordinator? _userLifecycle;

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
        IUserTombstoneGarbageCollector? garbageCollector = null,
        IUserPostLoginWorkScheduler? postLogin = null,
        IUserLifecycleCoordinator? userLifecycle = null,
        IServiceScopeFactory? scopes = null)
    {
        _scopes = scopes;
        _postLogin = postLogin;
        _userLifecycle = userLifecycle;
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

    public Task<NetworkDeltaApplyResult> ApplyAsync(NetworkDelta delta, CancellationToken ct = default)
    {
        if (_scopes is null)
            return ApplyCoreAsync(delta, ct);

        // Entity is only a lock-routing hint. ApplyCoreAsync still verifies its
        // exact agreement with the authenticated, decrypted payload before storage.
        // Open the persistence scope after waiting, not before: a context retained
        // by a multi-delta worker may otherwise contain an earlier canonical user,
        // checkpoint or origin revision despite acquiring the correct lifecycle lock.
        var parts = delta.Entity?.Split(':');
        if (_userLifecycle is not null && parts is { Length: 3 } &&
            parts[0] == nameof(SyncModelType.User) &&
            Guid.TryParseExact(parts[2], "N", out var userId) && userId != Guid.Empty)
            return _userLifecycle.ExecuteAsync(userId,
                token => ApplyInFreshScopeAsync(delta, token), ct);

        return ApplyInFreshScopeAsync(delta, ct);
    }

    private async Task<NetworkDeltaApplyResult> ApplyInFreshScopeAsync(NetworkDelta delta, CancellationToken ct)
    {
        const int maxAttempts = 3;
        for (var attempt = 1; ; attempt++)
        {
            ct.ThrowIfCancellationRequested();
            using var scope = _scopes!.CreateScope();
            var service = (NetworkDeltaService)scope.ServiceProvider.GetRequiredService<INetworkDeltaService>();
            try
            {
                return await service.ApplyCoreAsync(delta, ct);
            }
            catch (Exception ex) when (DeviceWriteConflict.IsStaleDevice(ex))
            {
                // Retry the authenticated delta with fresh device state. Durable inbox receipts
                // and replay checks handle work already committed before the device write.
                if (attempt == maxAttempts)
                    throw new SyncStateUnavailableException("A device record changed during synchronization.", ex);
            }
        }
    }

    private async Task<NetworkDeltaApplyResult> ApplyCoreAsync(NetworkDelta delta, CancellationToken ct)
    {
        if (!_identity.IsSyncOn)
            throw new SyncRouteDisabledException("Local synchronization is disabled.");

        var validated = await _protocol.ValidateAndReadAsync(delta, ct);
        var sourceDevice = validated.SourceDevice;
        var payload = validated.Payload;

        await _protocol.ValidateSourceAuthorizationAsync(sourceDevice, payload, ct);

        return await ApplyValidatedAsync(delta, sourceDevice, payload, ct);
    }

    private async Task<NetworkDeltaApplyResult> ApplyValidatedAsync(NetworkDelta delta, Device sourceDevice, SyncDeltaPayload payload, CancellationToken ct)
    {
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

        try
        {
            return _userLifecycle is null
                ? await MergeStoredSnapshotAsync(envelope, receipt, transportTimestamp, ct)
                : await _userLifecycle.ExecuteAsync(envelope.UserId,
                    workCt => MergeStoredSnapshotAsync(envelope, receipt, transportTimestamp, workCt), ct);
        }
        catch (Exception ex) when (ex is InvalidDataException or UnauthorizedAccessException or InvalidDataIntegrityException)
        {
            // The peer's signed envelope was already accepted and stored. A failure while
            // merging or refreshing our own canonical state cannot be attributed to it.
            throw new SyncStateUnavailableException("The verified snapshot is stored, but local merge is unavailable.", ex);
        }
    }
    private async Task<NetworkDeltaApplyResult> MergeStoredSnapshotAsync(
        UserSnapshotEnvelope envelope, UserSnapshotReceiptResult receipt, long transportTimestamp, CancellationToken ct)
    {
        var mergeCoordinator = _mergeCoordinator;
        var keyResolver = _keyResolver;
        var users = _users;
        // This gate is checked under lifecycle ownership, after any concurrent login completed.
        if (_postLogin?.IsInitialReadPending(envelope.UserId) == true)
        {
            _postLogin.Schedule(envelope.UserId);
            return new NetworkDeltaApplyResult(transportTimestamp, receipt);
        }
        var user = await users.GetByIdAsNoTrackingAsync(envelope.UserId, ct);
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

        var refreshedUser = await users.GetByIdWithRelationsAsync(envelope.UserId, ct);
        if (refreshedUser is not null)
            await _interactiveSessions.RefreshSyncedUserSessionsAsync(refreshedUser, ct);

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


        var mergedReceipt = receipt with
        {
            State = UserSnapshotReceiptState.MergedImmediately,
            Detail = "The pending snapshot was durably stored and then incorporated into canonical state."
        };
        return new NetworkDeltaApplyResult(transportTimestamp, mergedReceipt);
    }

}
