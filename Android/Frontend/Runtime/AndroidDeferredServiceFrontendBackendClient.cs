using PasswordManagerLocal.Common.Contracts.Endpoints;
using PasswordManagerLocal.Common.Contracts.Runtime;
using PasswordManagerLocal.Common.Contracts.BackgroundSync;

namespace PasswordManagerLocal.Android.Frontend;

public sealed class AndroidDeferredServiceFrontendBackendClient : IFrontendBackendClient<IEndpoints>
{
    private readonly AndroidActivityServiceAttachmentHandle _attachmentHandle;
    private readonly SemaphoreSlim _resolveLock = new(1, 1);
    private readonly object _snapshotGate = new();
    private IFrontendBackendClient<IEndpoints>? _inner;
    private BackendRuntimeSnapshot _snapshot = new(
        BackendRuntimeState.NotStarted,
        BackendRuntimeFailureKind.None,
        null,
        DateTimeOffset.MinValue);
    private volatile bool _disposed;

    public AndroidDeferredServiceFrontendBackendClient(
        AndroidActivityServiceAttachmentHandle attachmentHandle)
    {
        _attachmentHandle = attachmentHandle
            ?? throw new ArgumentNullException(nameof(attachmentHandle));
    }

    public BackendRuntimeSnapshot Snapshot
    {
        get
        {
            ThrowIfDisposed();

            if (Volatile.Read(ref _inner) is null &&
                _attachmentHandle.TryGetCompletedAttachment(out var attachment))
            {
                return attachment!.BackendClient.Snapshot;
            }

            lock (_snapshotGate)
                return _snapshot;
        }
    }

    public event EventHandler<BackendRuntimeStateChangedEventArgs>? StateChanged;

    public async Task ConnectAsync(CancellationToken cancellationToken = default) =>
        await (await ResolveAsync(cancellationToken)).ConnectAsync(cancellationToken);

    public async Task WaitUntilReadyAsync(CancellationToken cancellationToken = default) =>
        await (await ResolveAsync(cancellationToken)).WaitUntilReadyAsync(cancellationToken);

    public async Task ResetDatabaseAndRestartAsync(CancellationToken cancellationToken = default) =>
        await (await ResolveAsync(cancellationToken)).ResetDatabaseAndRestartAsync(cancellationToken);

    public async Task<IEndpoints> GetEndpointsAsync(CancellationToken cancellationToken = default) =>
        await (await ResolveAsync(cancellationToken)).GetEndpointsAsync(cancellationToken);

    public async ValueTask DisposeAsync()
    {
        IFrontendBackendClient<IEndpoints>? inner;
        lock (_snapshotGate)
        {
            if (_disposed)
                return;
            _disposed = true;
            inner = Interlocked.Exchange(ref _inner, null);
            StateChanged = null;
        }
        if (inner is not null)
            inner.StateChanged -= HandleInnerStateChanged;
        await _attachmentHandle.DisposeAsync();
        GC.SuppressFinalize(this);
    }

    private async Task<IFrontendBackendClient<IEndpoints>> ResolveAsync(
        CancellationToken cancellationToken)
    {
        ThrowIfDisposed();
        var inner = Volatile.Read(ref _inner);
        if (inner is not null)
            return inner;

        await _resolveLock.WaitAsync(cancellationToken);
        try
        {
            ThrowIfDisposed();
            inner = Volatile.Read(ref _inner);
            if (inner is not null)
                return inner;

            var attachment = await _attachmentHandle.GetAttachmentAsync(cancellationToken);
            lock (_snapshotGate)
            {
                ThrowIfDisposed();
                inner = attachment.BackendClient;
                inner.StateChanged += HandleInnerStateChanged;
                Volatile.Write(ref _inner, inner);
            }

            ApplyInnerSnapshot(inner.Snapshot);
            return inner;
        }
        finally
        {
            _resolveLock.Release();
        }
    }

    private void HandleInnerStateChanged(
        object? sender,
        BackendRuntimeStateChangedEventArgs args)
    {
        var current = sender is IBackendRuntimeClient runtimeClient
            ? runtimeClient.Snapshot
            : args.Current;
        ApplyInnerSnapshot(current);
    }

    private void ApplyInnerSnapshot(BackendRuntimeSnapshot current)
    {
        BackendRuntimeSnapshot previous;
        lock (_snapshotGate)
        {
            if (_disposed)
                return;

            previous = _snapshot;
            if (previous == current)
                return;

            _snapshot = current;
        }

        PublishStateChanged(new BackendRuntimeStateChangedEventArgs(previous, current));
    }

    private void PublishStateChanged(BackendRuntimeStateChangedEventArgs args)
    {
        var handlers = StateChanged;
        if (handlers is null)
            return;

        foreach (EventHandler<BackendRuntimeStateChangedEventArgs> handler in handlers.GetInvocationList())
        {
            try
            {
                handler(this, args);
            }
            catch
            {
            }
        }
    }

    private void ThrowIfDisposed()
    {
        if (_disposed)
            throw new ObjectDisposedException(nameof(AndroidDeferredServiceFrontendBackendClient));
    }
}
