using PasswordManagerLocal.Windows.Ipc.Protocol;

namespace PasswordManagerLocal.Windows.Ipc.Transport;

public sealed class WindowsIpcConnection : IWindowsIpcConnection
{
    private readonly Stream _stream;
    private readonly IpcFrameCodec _frameCodec;
    private readonly SemaphoreSlim _writeLock = new(1, 1);
    private readonly object _disposeGate = new();
    private Task? _disposeTask;
    private int _disposeStarted;

    public WindowsIpcConnection(
        Stream stream,
        IpcFrameCodec frameCodec,
        int? verifiedPeerProcessId = null)
    {
        _stream = stream ?? throw new ArgumentNullException(nameof(stream));
        _frameCodec = frameCodec ?? throw new ArgumentNullException(nameof(frameCodec));
        if (verifiedPeerProcessId is <= 0)
            throw new ArgumentOutOfRangeException(nameof(verifiedPeerProcessId));
        VerifiedPeerProcessId = verifiedPeerProcessId;
        ConnectionId = Guid.NewGuid();
    }

    public Guid ConnectionId { get; }
    public int? VerifiedPeerProcessId { get; }

    public bool IsConnected => Volatile.Read(ref _disposeStarted) == 0;

    public ValueTask<IpcFrame?> ReadFrameAsync(CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        return _frameCodec.ReadAsync(_stream, cancellationToken);
    }

    public async ValueTask WriteFrameAsync(
        IpcFrame frame,
        CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        await _writeLock.WaitAsync(cancellationToken);
        try
        {
            ThrowIfDisposed();
            cancellationToken.ThrowIfCancellationRequested();
            await WriteFrameUnderLockAsync(frame, cancellationToken);
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
        ThrowIfDisposed();
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
            ThrowIfDisposed();
            shutdownCancellationToken.ThrowIfCancellationRequested();
            if (!tryBeginWrite())
                return IpcFrameWriteResult.SkippedBeforeTransmission;

            await WriteFrameUnderLockAsync(frame, shutdownCancellationToken);
            return IpcFrameWriteResult.Written;
        }
        finally
        {
            _writeLock.Release();
        }
    }

    public ValueTask DisposeAsync()
    {
        lock (_disposeGate)
        {
            if (_disposeTask is null)
            {
                Volatile.Write(ref _disposeStarted, 1);
                _disposeTask = DisposeCoreAsync();
            }

            return new ValueTask(_disposeTask);
        }
    }

    private async ValueTask WriteFrameUnderLockAsync(
        IpcFrame frame,
        CancellationToken cancellationToken)
    {
        try
        {
            await _frameCodec.WriteAsync(_stream, frame, cancellationToken);
            await _stream.FlushAsync(cancellationToken);
        }
        catch (Exception exception)
        {
            Exception? disposalFailure = null;
            try
            {
                await DisposeAsync();
            }
            catch (Exception cleanupException)
            {
                disposalFailure = cleanupException;
            }

            if (disposalFailure is null)
                throw;

            throw new AggregateException(exception, disposalFailure);
        }
    }

    private async Task DisposeCoreAsync()
    {
        await _stream.DisposeAsync();
        GC.SuppressFinalize(this);
    }

    private void ThrowIfDisposed()
    {
        if (Volatile.Read(ref _disposeStarted) != 0)
            throw new ObjectDisposedException(nameof(WindowsIpcConnection));
    }
}
