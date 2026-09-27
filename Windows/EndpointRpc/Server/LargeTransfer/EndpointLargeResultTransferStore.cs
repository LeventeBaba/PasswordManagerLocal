using PasswordManagerLocal.Windows.EndpointRpc.Contracts;
using PasswordManagerLocal.Windows.EndpointRpc.Contracts.LargeTransfer;
using PasswordManagerLocal.Windows.EndpointRpc.Serialization;
using PasswordManagerLocal.Windows.Ipc.Lifecycle;

namespace PasswordManagerLocal.Windows.EndpointRpc.Server.LargeTransfer;

internal sealed class EndpointLargeResultTransferStore :
    IWindowsIpcConnectionLifecycleObserver,
    IAsyncDisposable
{
    private readonly Dictionary<Guid, EndpointLargeResultTransfer> _transfers = new();
    private readonly SemaphoreSlim _capacity = new(
        EndpointRpcLimits.MaximumConcurrentLargeTransfers,
        EndpointRpcLimits.MaximumConcurrentLargeTransfers);
    private readonly TimeProvider _timeProvider;
    private readonly ITimer _expirationTimer;
    private readonly object _gate = new();
    private int _disposed;

    public EndpointLargeResultTransferStore(TimeProvider? timeProvider = null)
    {
        _timeProvider = timeProvider ?? TimeProvider.System;
        var sweepInterval = TimeSpan.FromSeconds(
            Math.Max(1, EndpointRpcLimits.LargeResultTransferLifetimeSeconds / 4));
        _expirationTimer = _timeProvider.CreateTimer(
            static state => ((EndpointLargeResultTransferStore)state!).SweepExpired(),
            this,
            sweepInterval,
            sweepInterval);
    }

    internal int Count
    {
        get
        {
            lock (_gate)
                return _transfers.Count;
        }
    }

    public EndpointLargeResultDescriptor Create(
        EndpointRequestContext context,
        byte[] payload)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(payload);
        ThrowIfDisposed();
        if (payload.Length <= 0 || payload.Length > EndpointRpcLimits.MaximumLargeResultTotalBytes)
            throw new EndpointRpcPayloadException("The endpoint large-result payload exceeds the permitted size.");

        List<EndpointLargeResultTransfer> expired;
        lock (_gate)
            expired = RemoveExpiredLocked();
        DisposeAll(expired);

        EndpointLargeResultTransfer? transfer = null;
        lock (_gate)
        {
            var ownedCount = _transfers.Values.Count(item =>
                item.OwnerConnectionId == context.ConnectionId);
            if (ownedCount < EndpointRpcLimits.MaximumLargeTransfersPerConnection &&
                _capacity.Wait(0))
            {
                try
                {
                    transfer = new EndpointLargeResultTransfer(
                        context.ConnectionId,
                        context.PeerSessionId,
                        context.CorrelationId,
                        payload,
                        _timeProvider.GetUtcNow().AddSeconds(
                            EndpointRpcLimits.LargeResultTransferLifetimeSeconds),
                        () =>
                        {
                            _capacity.Release();
                        });
                    _transfers.Add(transfer.TransferId, transfer);
                }
                catch
                {
                    if (transfer is null)
                        _capacity.Release();
                    else
                        transfer.Dispose();
                    throw;
                }
            }
        }

        if (transfer is null)
            throw new EndpointLargeResultCapacityException();
        return transfer.CreateDescriptor();
    }

    public GetEndpointLargeResultChunkResponse GetChunk(
        Guid connectionId,
        Guid sessionId,
        GetEndpointLargeResultChunkRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        ThrowIfDisposed();
        EndpointLargeResultTransfer? invalidated = null;
        List<EndpointLargeResultTransfer>? expired = null;
        try
        {
            lock (_gate)
            {
                expired = RemoveExpiredLocked();
                if (!_transfers.TryGetValue(request.TransferId, out var transfer))
                    throw new EndpointRpcPayloadException("The endpoint large-result transfer is unavailable.");
                ValidateOwner(transfer, connectionId, sessionId, request.OriginalCorrelationId);
                try
                {
                    return transfer.ReadChunk(request.ChunkIndex);
                }
                catch
                {
                    _transfers.Remove(transfer.TransferId);
                    invalidated = transfer;
                    throw;
                }
            }
        }
        finally
        {
            if (invalidated is not null)
                invalidated.Dispose();
            if (expired is not null)
                DisposeAll(expired);
        }
    }

    public bool Release(
        Guid connectionId,
        Guid sessionId,
        ReleaseEndpointLargeResultRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        ThrowIfDisposed();
        EndpointLargeResultTransfer? released = null;
        List<EndpointLargeResultTransfer>? expired = null;
        try
        {
            lock (_gate)
            {
                expired = RemoveExpiredLocked();
                if (_transfers.TryGetValue(request.TransferId, out var transfer))
                {
                    ValidateOwner(transfer, connectionId, sessionId, request.OriginalCorrelationId);
                    _transfers.Remove(request.TransferId);
                    released = transfer;
                }
            }

            return released is not null;
        }
        finally
        {
            if (expired is not null)
                DisposeAll(expired);
            released?.Dispose();
        }
    }


    public bool InvalidateIfOwned(
        Guid connectionId,
        Guid sessionId,
        Guid transferId,
        long originalCorrelationId)
    {
        ThrowIfDisposed();
        EndpointLargeResultTransfer? invalidated = null;
        lock (_gate)
        {
            if (_transfers.TryGetValue(transferId, out var transfer) &&
                transfer.OwnerConnectionId == connectionId &&
                transfer.OwnerSessionId == sessionId &&
                transfer.OriginalCorrelationId == originalCorrelationId)
            {
                _transfers.Remove(transferId);
                invalidated = transfer;
            }
        }

        invalidated?.Dispose();
        return invalidated is not null;
    }

    public ValueTask OnConnectionLifecycleChangedAsync(
        IpcConnectionLifecycleNotification notification,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(notification);
        if (notification.State is not (IpcConnectionLifecycleState.Disconnected or IpcConnectionLifecycleState.Faulted))
            return ValueTask.CompletedTask;

        List<EndpointLargeResultTransfer> removed;
        lock (_gate)
        {
            removed = _transfers.Values
                .Where(item => item.OwnerConnectionId == notification.ConnectionId)
                .ToList();
            foreach (var transfer in removed)
                _transfers.Remove(transfer.TransferId);
        }

        DisposeAll(removed);
        return ValueTask.CompletedTask;
    }

    public ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
            return ValueTask.CompletedTask;

        _expirationTimer.Dispose();
        lock (_gate)
        {
            var transfers = _transfers.Values.ToArray();
            _transfers.Clear();
            DisposeAll(transfers);
        }
        _capacity.Dispose();
        return ValueTask.CompletedTask;
    }

    internal int SweepExpired()
    {
        if (Volatile.Read(ref _disposed) != 0)
            return 0;

        lock (_gate)
        {
            if (Volatile.Read(ref _disposed) != 0)
                return 0;
            var expired = RemoveExpiredLocked();
            DisposeAll(expired);
            return expired.Count;
        }
    }

    private List<EndpointLargeResultTransfer> RemoveExpiredLocked()
    {
        var now = _timeProvider.GetUtcNow();
        var expired = _transfers.Values.Where(item => item.IsExpired(now)).ToList();
        foreach (var transfer in expired)
            _transfers.Remove(transfer.TransferId);
        return expired;
    }

    private void ValidateOwner(
        EndpointLargeResultTransfer transfer,
        Guid connectionId,
        Guid sessionId,
        long originalCorrelationId)
    {
        if (transfer.OwnerConnectionId != connectionId ||
            transfer.OwnerSessionId != sessionId ||
            transfer.OriginalCorrelationId != originalCorrelationId)
        {
            throw new EndpointRpcPayloadException("The endpoint large-result transfer owner is invalid.");
        }
    }

    private void DisposeAll(IEnumerable<EndpointLargeResultTransfer> transfers)
    {
        foreach (var transfer in transfers)
            transfer.Dispose();
    }

    private void ThrowIfDisposed()
    {
        if (Volatile.Read(ref _disposed) != 0)
            throw new ObjectDisposedException(nameof(EndpointLargeResultTransferStore));
    }
}
