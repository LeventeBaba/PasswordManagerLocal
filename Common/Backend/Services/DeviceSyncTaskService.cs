using Microsoft.Extensions.DependencyInjection;
using PasswordManagerLocal.Common.Backend.Abstractions.Persistence;
using PasswordManagerLocal.Common.Backend.Abstractions.Repositories;
using PasswordManagerLocal.Common.Backend.Abstractions.Services;
using PasswordManagerLocal.Common.Backend.Models;
using PasswordManagerLocal.Common.Backend.Constants;
using PasswordManagerLocal.Common.Backend.Sync;
using System.Collections.Concurrent;
using PasswordManagerLocal.Common.Backend.Sync.Discovery;

using PasswordManagerLocal.Common.Backend.Utils;
using PasswordManagerLocal.Common.Backend.Abstractions.Sync.Discovery;

using PasswordManagerLocal.Common.Backend.Internal.Sync;

using PasswordManagerLocal.Common.Backend.Diagnostics;
namespace PasswordManagerLocal.Common.Backend.Services;

public sealed class DeviceSyncTaskService : IDeviceSyncTaskService, IDisposable
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ISyncTransportClientService _syncTransport;
    private readonly ISyncDeviceIdentityService _syncDeviceIdentities;
    private readonly IDiscoveredDeviceEndpointRegistry _endpointRegistry;
    private readonly IDeviceIdentityService _identity;
    private readonly ConcurrentDictionary<Guid, byte> _runningDeviceIds = new();
    private readonly ConcurrentDictionary<Guid, Task> _runningTasks = new();
    private readonly Dictionary<Guid, PendingDeviceSyncStart> _pendingStarts = new();
    private readonly Dictionary<Guid, TaskCompletionSource<bool>> _idleSignals = new();
    private readonly object _runtimeLock = new();
    private CancellationTokenSource _runtimeCancellation = new();

    public DeviceSyncTaskService(
        IServiceScopeFactory scopeFactory,
        ISyncTransportClientService syncTransport,
        ISyncDeviceIdentityService syncDeviceIdentities,
        IDiscoveredDeviceEndpointRegistry endpointRegistry,
        IDeviceIdentityService identity)
    {
        _scopeFactory = scopeFactory;
        _syncTransport = syncTransport;
        _syncDeviceIdentities = syncDeviceIdentities;
        _endpointRegistry = endpointRegistry;
        _identity = identity;
    }


    public bool TryStart(DiscoveredDeviceEndpoint endpoint, Device device)
    {
        if (!_identity.IsSyncOn)
            return false;

        if (device.Id == Guid.Empty)
            return false;

        if (!device.IsTrusted || device.IsBlocked)
            return false;

        if (IsLocalDevice(device))
            return false;

        if (string.IsNullOrWhiteSpace(endpoint.Host))
            return false;

        if (endpoint.Port <= 0)
            return false;

        if (string.IsNullOrWhiteSpace(endpoint.TlsCertFingerprint))
            return false;

        Device targetDevice;
        CancellationToken token;
        lock (_runtimeLock)
        {
            if (!_identity.IsSyncOn || _runtimeCancellation.IsCancellationRequested)
                return false;

            if (_runningDeviceIds.ContainsKey(device.Id))
            {
                _pendingStarts[device.Id] = new PendingDeviceSyncStart(CloneEndpoint(endpoint), CloneDevice(device));
                return false;
            }

            _runningDeviceIds[device.Id] = 0;
            if (!_idleSignals.ContainsKey(device.Id))
            {
                _idleSignals[device.Id] = new TaskCompletionSource<bool>(
                    TaskCreationOptions.RunContinuationsAsynchronously);
            }

            token = _runtimeCancellation.Token;
            targetDevice = CloneDevice(device);
            var task = Task.Run(() => RunAsync(CloneEndpoint(endpoint), targetDevice, token), CancellationToken.None);
            _runningTasks[device.Id] = task;
        }

        return true;
    }


    public Task WaitForIdleAsync(Guid deviceId, CancellationToken ct = default)
    {
        if (deviceId == Guid.Empty)
            return Task.CompletedTask;

        Task idleTask;
        lock (_runtimeLock)
        {
            if (!_idleSignals.TryGetValue(deviceId, out var signal))
                return Task.CompletedTask;

            idleTask = signal.Task;
        }

        return ct.CanBeCanceled
            ? idleTask.WaitAsync(ct)
            : idleTask;
    }


    public async Task StopAllAsync(CancellationToken ct = default)
    {
        Task[] runningTasks;

        lock (_runtimeLock)
        {
            _runtimeCancellation.Cancel();
            runningTasks = _runningTasks.Values.ToArray();
        }

        if (runningTasks.Length != 0)
        {
            try
            {
                await Task.WhenAny(Task.WhenAll(runningTasks), Task.Delay(TimeSpan.FromSeconds(5), ct));
            }
            catch
            {
            }
        }

        TaskCompletionSource<bool>[] idleSignals;
        lock (_runtimeLock)
        {
            _runningDeviceIds.Clear();
            _runningTasks.Clear();
            _pendingStarts.Clear();
            idleSignals = _idleSignals.Values.ToArray();
            _idleSignals.Clear();
            _runtimeCancellation.Dispose();
            _runtimeCancellation = new CancellationTokenSource();
        }

        foreach (var signal in idleSignals)
            signal.TrySetResult(true);
    }


    public void Dispose()
    {
        TaskCompletionSource<bool>[] idleSignals;
        lock (_runtimeLock)
        {
            _runtimeCancellation.Cancel();
            _pendingStarts.Clear();
            idleSignals = _idleSignals.Values.ToArray();
            _idleSignals.Clear();
            _runtimeCancellation.Dispose();
        }

        foreach (var signal in idleSignals)
            signal.TrySetResult(true);
    }


    private async Task RunAsync(DiscoveredDeviceEndpoint endpoint, Device targetDevice, CancellationToken ct)
    {
        BackendDebugLog.Info(
            $"Outgoing synchronization worker started. TargetDeviceId={targetDevice.Id:N}, Endpoint={endpoint.Host}:{endpoint.Port}.",
            "Synchronization");

        try
        {
            // Authoritative epoch/membership barriers must arrive before ordinary snapshots that
            // depend on them. This avoids deleting a queue item after a receiver correctly rejects
            // a newer-epoch ordinary snapshot.
            if (!ct.IsCancellationRequested && _identity.IsSyncOn &&
                !await TryRunControlOperationAntiEntropyAsync(endpoint, targetDevice, ct))
            {
                return;
            }

            while (!ct.IsCancellationRequested && _identity.IsSyncOn &&
                   await TrySendNextAsync(endpoint, targetDevice, ct))
            {
            }

            // A false send result can mean that transport or target validation failed, not only
            // that the queue is empty. If ordinary durable work remains, stop this worker session
            // and let discovery/a later kick retry it. Continuing into anti-entropy and the final
            // drain used to send the same failed batch again immediately.
            if (!ct.IsCancellationRequested && _identity.IsSyncOn &&
                await HasPendingAsync(targetDevice.Id, ct))
            {
                return;
            }

            if (!ct.IsCancellationRequested && _identity.IsSyncOn &&
                !await TryRunAntiEntropyAsync(endpoint, targetDevice, ct))
            {
                return;
            }

            // A successful immediate merge can publish a fresh local origin revision and enqueue
            // ordinary outgoing work. Drain that work before ending this target-device session.
            while (!ct.IsCancellationRequested && _identity.IsSyncOn &&
                   await TrySendNextAsync(endpoint, targetDevice, ct))
            {
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            BackendDebugLog.Error($"Outgoing synchronization worker for device {targetDevice.Id:N} failed: {ex.Message}", ex);
        }
        finally
        {
            PendingDeviceSyncStart? pendingStart;
            lock (_runtimeLock)
            {
                _runningDeviceIds.TryRemove(targetDevice.Id, out _);
                _runningTasks.TryRemove(targetDevice.Id, out _);
                _pendingStarts.Remove(targetDevice.Id, out pendingStart);
            }

            try
            {
                var hasPending = await HasPendingAsync(targetDevice.Id, CancellationToken.None);
                if (hasPending && pendingStart is not null && _identity.IsSyncOn)
                    TryStart(pendingStart.Endpoint, pendingStart.Device);
                else if (!hasPending)
                    _syncDeviceIdentities.TryRemove(targetDevice);

                BackendDebugLog.Info(
                    $"Outgoing synchronization worker session finished. TargetDeviceId={targetDevice.Id:N}, " +
                    $"PendingWork={hasPending}, RestartScheduled={hasPending && pendingStart is not null && _identity.IsSyncOn}.",
                    "Synchronization");
            }
            catch (Exception ex)
            {
                BackendDebugLog.Error($"Outgoing synchronization worker cleanup for device {targetDevice.Id:N} failed: {ex.Message}", ex);
            }
            finally
            {
                TaskCompletionSource<bool>? idleSignal = null;
                lock (_runtimeLock)
                {
                    // An external start or a remembered kick may already have launched the next
                    // session. Keep the same signal alive until the complete per-device chain is idle.
                    if (!_runningDeviceIds.ContainsKey(targetDevice.Id) &&
                        !_pendingStarts.ContainsKey(targetDevice.Id) &&
                        _idleSignals.Remove(targetDevice.Id, out var signal))
                    {
                        idleSignal = signal;
                    }
                }

                idleSignal?.TrySetResult(true);
            }
        }
    }


    private async Task<bool> TrySendNextAsync(DiscoveredDeviceEndpoint endpoint, Device targetDevice, CancellationToken ct)
    {
        using var scope = _scopeFactory.CreateScope();

        var queue = scope.ServiceProvider.GetRequiredService<ISyncQueueRepository>();
        var deltaBuilder = scope.ServiceProvider.GetRequiredService<IOutgoingDeltaBuilderService>();
        var authorization = scope.ServiceProvider.GetRequiredService<ISyncAuthorizationService>();
        var uow = scope.ServiceProvider.GetRequiredService<IUnitOfWork>();

        if (!_identity.IsSyncOn)
            return false;

        var queueItems = await queue.GetNextPendingBatchForDeviceAsync(targetDevice.Id, SyncConstants.PendingDeviceDeltaBatchSize, ct);
        if (queueItems.Count == 0)
            return false;

        var validItems = new List<(SyncQueueItem QueueItem, SyncItem SyncItem)>();
        foreach (var queueItem in queueItems)
        {
            if (queueItem.SyncItem is null || !await authorization.CanSendAsync(queueItem.SyncItem, targetDevice.Id, ct))
            {
                queue.Delete(queueItem);
                continue;
            }

            validItems.Add((queueItem, queueItem.SyncItem));
        }

        if (validItems.Count == 0)
        {
            await uow.SaveChangesAsync(ct);
            return true;
        }

        if (!await RefreshAndValidateTargetDeviceAsync(scope.ServiceProvider, targetDevice, endpoint, ct))
            return false;

        var sendItems = new List<(SyncQueueItem QueueItem, SyncItem SyncItem, NetworkDelta Delta)>();
        long totalPayloadBytes = 0;
        foreach (var item in validItems)
        {
            try
            {
                var delta = await deltaBuilder.BuildAsync(item.SyncItem, targetDevice, ct);
                if (totalPayloadBytes + delta.Payload.Length > SyncConstants.MaxIncomingDeltaTotalBytesPerCall)
                    break;

                totalPayloadBytes += delta.Payload.Length;
                sendItems.Add((item.QueueItem, item.SyncItem, delta));
            }
            catch (InvalidOperationException ex)
            {
                BackendDebugLog.Error(
                    $"Discarding invalid outgoing sync item {item.SyncItem.Id:N} ({item.SyncItem.ModelType}/{item.SyncItem.ChangeType}) for device {targetDevice.Id:N}: {ex.Message}",
                    ex);
                queue.Delete(item.QueueItem);
            }
            catch (InvalidDataException ex)
            {
                BackendDebugLog.Error(
                    $"Discarding malformed outgoing sync item {item.SyncItem.Id:N} ({item.SyncItem.ModelType}/{item.SyncItem.ChangeType}) for device {targetDevice.Id:N}: {ex.Message}",
                    ex);
                queue.Delete(item.QueueItem);
            }
        }

        if (sendItems.Count == 0)
        {
            await uow.SaveChangesAsync(ct);
            return true;
        }

        if (!_identity.IsSyncOn)
            return false;

        for (var i = sendItems.Count - 1; i >= 0; i--)
        {
            var item = sendItems[i];
            if (await authorization.CanSendAsync(item.SyncItem, targetDevice.Id, ct))
                continue;

            queue.Delete(item.QueueItem);
            sendItems.RemoveAt(i);
        }

        if (sendItems.Count == 0)
        {
            await uow.SaveChangesAsync(ct);
            return true;
        }

        var sent = await _syncTransport.SendDeltasAsync(
            endpoint.Host,
            endpoint.Port,
            targetDevice.TlsCertFingerprint,
            sendItems.Select(x => x.Delta).ToArray(),
            ct);

        if (!sent)
        {
            _endpointRegistry.TryRemove(endpoint.TlsCertFingerprint);
            return false;
        }

        foreach (var item in sendItems)
            queue.Delete(item.QueueItem);

        await uow.SaveChangesAsync(ct);

        foreach (var item in sendItems)
            await CleanupDetachedDeviceIfSyncCompletedAsync(scope.ServiceProvider, item.SyncItem, targetDevice.Id, ct);

        BackendDebugLog.Info(
            $"Outgoing synchronization delta batch completed successfully. TargetDeviceId={targetDevice.Id:N}, " +
            $"DeltaCount={sendItems.Count}, PayloadBytes={totalPayloadBytes}.",
            "Synchronization");

        return true;
    }


    private async Task<bool> TryRunControlOperationAntiEntropyAsync(
        DiscoveredDeviceEndpoint endpoint,
        Device targetDevice,
        CancellationToken ct)
    {
        using var scope = _scopeFactory.CreateScope();
        var antiEntropy = scope.ServiceProvider.GetService<IUserControlOperationAntiEntropyService>();
        if (antiEntropy is null)
            return true;

        if (!await RefreshAndValidateTargetDeviceAsync(scope.ServiceProvider, targetDevice, endpoint, ct))
            return false;

        var localInventory = await antiEntropy.BuildInventoryAsync(targetDevice.Id, ct);
        var exchange = await _syncTransport.ExchangeUserControlOperationInventoryAsync(
            endpoint.Host,
            endpoint.Port,
            targetDevice.TlsCertFingerprint,
            localInventory,
            ct);

        if (exchange.RequestedOperations.Count > 0)
        {
            var requestedDeltas = await antiEntropy.BuildRequestedOperationDeltasAsync(
                targetDevice.Id,
                exchange.RequestedOperations,
                ct);
            if (requestedDeltas.Count != exchange.RequestedOperations.Count)
                throw new InvalidDataException("Not every requested control operation could be relayed exactly.");

            if (!await _syncTransport.SendDeltasAsync(
                    endpoint.Host,
                    endpoint.Port,
                    targetDevice.TlsCertFingerprint,
                    requestedDeltas,
                    ct))
            {
                _endpointRegistry.TryRemove(endpoint.TlsCertFingerprint);
                return false;
            }
        }

        var missing = antiEntropy.FindMissingOperations(localInventory, exchange.Users);
        if (missing.Count == 0)
            return true;

        var requestBatch = new UserControlOperationRequestBatch();
        requestBatch.Requests.AddRange(missing);
        var receivedDeltas = await _syncTransport.RequestUserControlOperationsAsync(
            endpoint.Host,
            endpoint.Port,
            targetDevice.TlsCertFingerprint,
            requestBatch,
            ct);
        if (receivedDeltas.Count == 0)
            return false;

        var expected = missing.ToDictionary(
            request => ParseControlRequestKey(request),
            request => request.ExpectedOperationHash.ToByteArray());
        var fulfilled = new HashSet<Guid>();
        var applier = scope.ServiceProvider.GetRequiredService<IIncomingDeltaApplierService>();
        foreach (var delta in receivedDeltas.OrderBy(delta => delta.Ts))
        {
            var result = await applier.ApplyAsync(delta, ct);
            var receipt = result.UserControlOperationReceipt
                ?? throw new InvalidDataException("A relayed control operation did not produce an explicit receipt.");
            if (!expected.TryGetValue(receipt.OperationId, out var expectedHash) ||
                !System.Security.Cryptography.CryptographicOperations.FixedTimeEquals(expectedHash, receipt.OperationHash))
            {
                throw new InvalidDataException("A relayed control operation did not match an exact outstanding request.");
            }
            if (!IsDurableControlOperationReceipt(receipt.State))
                throw new InvalidDataException($"A relayed control operation was not durably accepted: {receipt.State}.");
            if (!fulfilled.Add(receipt.OperationId))
                throw new InvalidDataException("The relay returned the same requested control operation more than once.");
        }

        return fulfilled.Count == expected.Count;
    }

    private Guid ParseControlRequestKey(UserControlOperationRequest request)
    {
        if (!Guid.TryParse(request.OperationId, out var operationId) || operationId == Guid.Empty)
            throw new InvalidDataException("The control-operation request identity is invalid.");
        return operationId;
    }

    private bool IsDurableControlOperationReceipt(UserControlOperationReceiptState state) =>
        state is UserControlOperationReceiptState.StoredPending or
            UserControlOperationReceiptState.AlreadyStored or
            UserControlOperationReceiptState.Applied or
            UserControlOperationReceiptState.Obsolete;


    private async Task<bool> TryRunAntiEntropyAsync(
        DiscoveredDeviceEndpoint endpoint,
        Device targetDevice,
        CancellationToken ct)
    {
        using var scope = _scopeFactory.CreateScope();
        var antiEntropy = scope.ServiceProvider.GetService<IUserSnapshotAntiEntropyService>();
        if (antiEntropy is null)
            return true;

        if (!await RefreshAndValidateTargetDeviceAsync(scope.ServiceProvider, targetDevice, endpoint, ct))
            return false;

        var localInventory = await antiEntropy.BuildInventoryAsync(targetDevice.Id, ct);
        var exchange = await _syncTransport.ExchangeUserSnapshotInventoryAsync(
            endpoint.Host,
            endpoint.Port,
            targetDevice.TlsCertFingerprint,
            localInventory,
            ct);

        if (exchange.RequestedSnapshots.Count > 0)
        {
            var requestedDeltas = await antiEntropy.BuildRequestedSnapshotDeltasAsync(
                targetDevice.Id,
                exchange.RequestedSnapshots,
                ct);
            if (requestedDeltas.Count != exchange.RequestedSnapshots.Count)
                throw new InvalidDataException("Not every requested user snapshot could be relayed exactly.");

            if (!await _syncTransport.SendDeltasAsync(
                    endpoint.Host,
                    endpoint.Port,
                    targetDevice.TlsCertFingerprint,
                    requestedDeltas,
                    ct))
            {
                _endpointRegistry.TryRemove(endpoint.TlsCertFingerprint);
                return false;
            }
        }

        var missing = antiEntropy.FindMissingSnapshots(localInventory, exchange.Users);
        if (missing.Count == 0)
            return true;

        var requestBatch = new UserSnapshotRequestBatch();
        requestBatch.Requests.AddRange(missing);
        var receivedDeltas = await _syncTransport.RequestUserSnapshotsAsync(
            endpoint.Host,
            endpoint.Port,
            targetDevice.TlsCertFingerprint,
            requestBatch,
            ct);
        if (receivedDeltas.Count == 0)
            return false;

        var expected = missing.ToDictionary(
            request => BuildSnapshotRequestKey(request),
            request => request.ExpectedSnapshotHash.ToByteArray());
        var fulfilled = new HashSet<(Guid UserId, Guid OriginDeviceId, Guid OriginInstanceId, long Revision)>();
        var applier = scope.ServiceProvider.GetRequiredService<IIncomingDeltaApplierService>();

        foreach (var delta in receivedDeltas.OrderBy(delta => delta.Ts))
        {
            var result = await applier.ApplyAsync(delta, ct);
            var receipt = result.UserSnapshotReceipt
                ?? throw new InvalidDataException("A relayed user snapshot did not produce an explicit receipt.");
            var key = (receipt.UserId, receipt.OriginDeviceId, receipt.OriginInstanceId, receipt.OriginRevision);
            if (!expected.TryGetValue(key, out var expectedHash) ||
                !System.Security.Cryptography.CryptographicOperations.FixedTimeEquals(expectedHash, receipt.SnapshotHash))
            {
                throw new InvalidDataException("A relayed user snapshot did not match an exact outstanding request.");
            }

            if (!IsDurableSnapshotReceipt(receipt.State))
                throw new InvalidDataException($"A relayed user snapshot was not durably accepted: {receipt.State}.");
            if (!fulfilled.Add(key))
                throw new InvalidDataException("The relay returned the same requested user snapshot more than once.");
        }

        return fulfilled.Count == expected.Count;
    }


    private (Guid UserId, Guid OriginDeviceId, Guid OriginInstanceId, long Revision) BuildSnapshotRequestKey(
        UserSnapshotRequest request)
    {
        if (!Guid.TryParse(request.UserId, out var userId) ||
            !Guid.TryParse(request.OriginDeviceId, out var originDeviceId) ||
            !Guid.TryParse(request.OriginInstanceId, out var originInstanceId))
        {
            throw new InvalidDataException("The user snapshot request identity is invalid.");
        }

        return (userId, originDeviceId, originInstanceId, request.OriginRevision);
    }


    private bool IsDurableSnapshotReceipt(UserSnapshotReceiptState state) =>
        state is UserSnapshotReceiptState.StoredPending or
            UserSnapshotReceiptState.ReplacedOlderPending or
            UserSnapshotReceiptState.AlreadyStored or
            UserSnapshotReceiptState.StoredMergedReceipt or
            UserSnapshotReceiptState.MergedImmediately or
            UserSnapshotReceiptState.ObsoleteRevision or
            UserSnapshotReceiptState.RejectedAccountDeleted;


    private async Task<bool> RefreshAndValidateTargetDeviceAsync(IServiceProvider services, Device targetDevice, DiscoveredDeviceEndpoint endpoint, CancellationToken ct)
    {
        var devices = services.GetRequiredService<IDeviceRepository>();
        var freshDevice = await devices.GetByIdWithUserDevicesAsync(targetDevice.Id, ct);
        if (!_identity.IsSyncOn || freshDevice is null || !freshDevice.IsTrusted || freshDevice.IsBlocked || IsLocalDevice(freshDevice))
        {
            _syncDeviceIdentities.TryRemove(targetDevice);
            return false;
        }

        if (freshDevice.PublicKey.Length == 0 ||
            freshDevice.SignPublicKey.Length == 0 ||
            string.IsNullOrWhiteSpace(freshDevice.TlsCertFingerprint) ||
            !string.Equals(FingerprintUtil.NormalizeOrEmpty(freshDevice.TlsCertFingerprint), FingerprintUtil.NormalizeOrEmpty(endpoint.TlsCertFingerprint), StringComparison.OrdinalIgnoreCase))
        {
            _syncDeviceIdentities.TryRemove(freshDevice);
            return false;
        }


        targetDevice.PublicKey = freshDevice.PublicKey.ToArray();
        targetDevice.SignPublicKey = freshDevice.SignPublicKey.ToArray();
        targetDevice.TlsCertFingerprint = freshDevice.TlsCertFingerprint;
        targetDevice.LastKnownHash = freshDevice.LastKnownHash.ToArray();
        targetDevice.LastSync = UtcDateTimeUtil.ToUtc(freshDevice.LastSync);
        targetDevice.LastSeen = UtcDateTimeUtil.ToUtc(freshDevice.LastSeen);
        targetDevice.IsTrusted = freshDevice.IsTrusted;
        targetDevice.IsBlocked = freshDevice.IsBlocked;
        targetDevice.BlockedReason = freshDevice.BlockedReason;
        targetDevice.BlockedAt = UtcDateTimeUtil.ToUtc(freshDevice.BlockedAt);
        targetDevice.InvalidSyncAttemptCount = freshDevice.InvalidSyncAttemptCount;
        targetDevice.LastInvalidSyncAttemptAt = UtcDateTimeUtil.ToUtc(freshDevice.LastInvalidSyncAttemptAt);
        targetDevice.LastModifiedAt = UtcDateTimeUtil.ToUtc(freshDevice.LastModifiedAt);
        targetDevice.IntegrityHash = freshDevice.IntegrityHash.ToArray();

        return true;
    }


    private async Task CleanupDetachedDeviceIfSyncCompletedAsync(IServiceProvider services, SyncItem syncItem, Guid targetDeviceId, CancellationToken ct)
    {
        if (syncItem.ModelType != SyncModelType.UserDevice || syncItem.ChangeType != SyncChangeType.Deleted)
            return;

        var queue = services.GetRequiredService<ISyncQueueRepository>();
        if (await queue.HasPendingForSyncItemAsync(syncItem.Id, ct))
            return;

        var userDevices = services.GetRequiredService<IUserDeviceRepository>();
        var devices = services.GetRequiredService<IDeviceRepository>();
        var tombstones = services.GetRequiredService<ISyncTombstoneRepository>();
        var uow = services.GetRequiredService<IUnitOfWork>();

        var userDevice = await userDevices.GetByModelIdAsync(syncItem.ModelId, ct);
        if (userDevice is null || !userDevice.IsDeleted)
            return;

        await tombstones.UpsertAsync(syncItem.ModelId, SyncModelType.UserDevice, syncItem.ChangedAtTs, ct);

        if (!await userDevices.HasAnyActiveLinkForDeviceAsync(userDevice.DeviceId, ct))
        {
            var device = await devices.GetByIdWithUserDevicesAsync(userDevice.DeviceId, ct);
            if (device is not null)
            {
                _syncDeviceIdentities.TryRemove(device);
                devices.Delete(device);
            }
        }
        else
        {
            userDevices.Delete(userDevice);
        }

        await uow.SaveChangesAsync(ct);
    }


    private async Task<bool> HasPendingAsync(Guid deviceId, CancellationToken ct)
    {
        using var scope = _scopeFactory.CreateScope();
        var queue = scope.ServiceProvider.GetRequiredService<ISyncQueueRepository>();
        if (await queue.HasPendingForDeviceAsync(deviceId, ct))
            return true;

        var operations = scope.ServiceProvider.GetService<IUserControlOperationRepository>();
        var membership = scope.ServiceProvider.GetService<IUserMembershipAuthorizationRepository>();
        if (operations is null || membership is null)
            return false;
        var deletedUserIds = await operations.ListAppliedAccountDeletionUserIdsAsync(ct);
        if (deletedUserIds.Count == 0)
            return false;

        var historicallyAuthorizedUsers = await membership.ListUserIdsForDeviceAsync(deviceId, ct);
        return historicallyAuthorizedUsers.Any(userId => deletedUserIds.Contains(userId));
    }


    private bool IsLocalDevice(Device device)
    {
        if (!_identity.IsInitialized)
            return false;

        if (device.Id == _identity.LocalDeviceId)
            return true;

        if (device.SignPublicKey.SequenceEqual(_identity.SignPublicKey))
            return true;

        return string.Equals(FingerprintUtil.NormalizeOrEmpty(device.TlsCertFingerprint), FingerprintUtil.NormalizeOrEmpty(_identity.FingerprintHex), StringComparison.OrdinalIgnoreCase);
    }


    private Device CloneDevice(Device source) =>
        new()
        {
            Id = source.Id,
            PublicKey = source.PublicKey.ToArray(),
            SignPublicKey = source.SignPublicKey.ToArray(),
            TlsCertFingerprint = source.TlsCertFingerprint,
            DeviceType = source.DeviceType,
            LastKnownHash = source.LastKnownHash.ToArray(),
            LastSync = UtcDateTimeUtil.ToUtc(source.LastSync),
            LastSeen = UtcDateTimeUtil.ToUtc(source.LastSeen),
            IsTrusted = source.IsTrusted,
            IsBlocked = source.IsBlocked,
            BlockedReason = source.BlockedReason,
            BlockedAt = UtcDateTimeUtil.ToUtc(source.BlockedAt),
            InvalidSyncAttemptCount = source.InvalidSyncAttemptCount,
            LastInvalidSyncAttemptAt = UtcDateTimeUtil.ToUtc(source.LastInvalidSyncAttemptAt),
            IntegrityHash = source.IntegrityHash.ToArray(),
            LastModifiedAt = UtcDateTimeUtil.ToUtc(source.LastModifiedAt)
        };


    private DiscoveredDeviceEndpoint CloneEndpoint(DiscoveredDeviceEndpoint source) =>
        new()
        {
            Host = source.Host,
            Port = source.Port,
            TlsCertFingerprint = source.TlsCertFingerprint
        };


}
