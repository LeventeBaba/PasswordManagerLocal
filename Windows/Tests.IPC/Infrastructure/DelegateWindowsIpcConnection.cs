using PasswordManagerLocal.Windows.Ipc.Protocol;
using PasswordManagerLocal.Windows.Ipc.Transport;

namespace PasswordManagerLocal.Windows.Tests.IPC.Infrastructure;

internal sealed class DelegateWindowsIpcConnection : IWindowsIpcConnection
{
    private readonly Func<CancellationToken, ValueTask<IpcFrame?>> _read;
    private readonly Func<IpcFrame, CancellationToken, ValueTask> _write;
    private readonly Func<ValueTask> _dispose;
    private readonly SemaphoreSlim _writeLock = new(1, 1);
    private int _disposeStarted;

    public DelegateWindowsIpcConnection(
        Func<CancellationToken, ValueTask<IpcFrame?>> read,
        Func<IpcFrame, CancellationToken, ValueTask> write,
        Func<ValueTask> dispose)
    {
        _read = read ?? throw new ArgumentNullException(nameof(read));
        _write = write ?? throw new ArgumentNullException(nameof(write));
        _dispose = dispose ?? throw new ArgumentNullException(nameof(dispose));
        ConnectionId = Guid.NewGuid();
    }

    public Guid ConnectionId { get; }
    public int? VerifiedPeerProcessId => null;
    public bool IsConnected => Volatile.Read(ref _disposeStarted) == 0;

    public ValueTask<IpcFrame?> ReadFrameAsync(CancellationToken cancellationToken = default) =>
        _read(cancellationToken);

    public async ValueTask WriteFrameAsync(
        IpcFrame frame,
        CancellationToken cancellationToken = default)
    {
        await _writeLock.WaitAsync(cancellationToken);
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            await _write(frame, cancellationToken);
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
            if (!tryBeginWrite())
                return IpcFrameWriteResult.SkippedBeforeTransmission;

            await _write(frame, shutdownCancellationToken);
            return IpcFrameWriteResult.Written;
        }
        finally
        {
            _writeLock.Release();
        }
    }

    public ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposeStarted, 1) != 0)
            return ValueTask.CompletedTask;

        return _dispose();
    }
}
