using PasswordManagerLocal.Windows.Ipc.Protocol;
using PasswordManagerLocal.Windows.Ipc.Transport;
using System.Threading.Channels;

namespace PasswordManagerLocal.Windows.Tests.IPC.Infrastructure;

internal sealed class InMemoryIpcConnection : IWindowsIpcConnection
{
    private readonly ChannelReader<IpcFrame> _incoming;
    private readonly ChannelWriter<IpcFrame> _outgoing;
    private readonly SemaphoreSlim _writeLock = new(1, 1);
    private int _disposeStarted;
    private int _disposeCallCount;

    public InMemoryIpcConnection(
        ChannelReader<IpcFrame> incoming,
        ChannelWriter<IpcFrame> outgoing)
    {
        _incoming = incoming;
        _outgoing = outgoing;
        ConnectionId = Guid.NewGuid();
    }

    public Guid ConnectionId { get; }
    public int? VerifiedPeerProcessId => null;
    public bool IsConnected => Volatile.Read(ref _disposeStarted) == 0;
    public Exception? DisposeException { get; set; }
    public int DisposeCallCount => Volatile.Read(ref _disposeCallCount);

    public async ValueTask<IpcFrame?> ReadFrameAsync(
        CancellationToken cancellationToken = default)
    {
        try
        {
            if (!await _incoming.WaitToReadAsync(cancellationToken))
                return null;

            return _incoming.TryRead(out var frame) ? frame : null;
        }
        catch (ChannelClosedException exception) when (exception.InnerException is not null)
        {
            throw exception.InnerException!;
        }
    }

    public async ValueTask WriteFrameAsync(
        IpcFrame frame,
        CancellationToken cancellationToken = default)
    {
        await _writeLock.WaitAsync(cancellationToken);
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            ThrowIfDisposed();
            await _outgoing.WriteAsync(frame, cancellationToken);
        }
        finally
        {
            _writeLock.Release();
        }
    }

    public async ValueTask<IpcFrameWriteResult> TryWriteFrameAsync(
        IpcFrame frame,
        Func<bool> tryBeginWrite,
        CancellationToken queuedCancellationToken,
        CancellationToken shutdownCancellationToken)
    {
        ArgumentNullException.ThrowIfNull(tryBeginWrite);
        using var admissionSource = CancellationTokenSource.CreateLinkedTokenSource(
            queuedCancellationToken,
            shutdownCancellationToken);
        try
        {
            await _writeLock.WaitAsync(admissionSource.Token);
        }
        catch (OperationCanceledException)
            when (shutdownCancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (OperationCanceledException)
            when (queuedCancellationToken.IsCancellationRequested)
        {
            return IpcFrameWriteResult.SkippedBeforeTransmission;
        }

        try
        {
            shutdownCancellationToken.ThrowIfCancellationRequested();
            ThrowIfDisposed();
            if (!tryBeginWrite())
                return IpcFrameWriteResult.SkippedBeforeTransmission;

            await _outgoing.WriteAsync(frame, shutdownCancellationToken);
            return IpcFrameWriteResult.Written;
        }
        finally
        {
            _writeLock.Release();
        }
    }

    public ValueTask DisposeAsync()
    {
        Interlocked.Increment(ref _disposeCallCount);
        if (Interlocked.Exchange(ref _disposeStarted, 1) == 0)
        {
            _outgoing.TryComplete();
            if (DisposeException is not null)
                return ValueTask.FromException(DisposeException);
        }

        return ValueTask.CompletedTask;
    }

    public void Fault(Exception exception)
    {
        ArgumentNullException.ThrowIfNull(exception);
        Interlocked.Exchange(ref _disposeStarted, 1);
        _outgoing.TryComplete(exception);
    }

    private void ThrowIfDisposed()
    {
        if (Volatile.Read(ref _disposeStarted) != 0)
            throw new ObjectDisposedException(nameof(InMemoryIpcConnection));
    }
}
