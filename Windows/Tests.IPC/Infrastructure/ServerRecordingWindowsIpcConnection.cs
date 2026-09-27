using PasswordManagerLocal.Windows.Ipc.Protocol;
using PasswordManagerLocal.Windows.Ipc.Transport;
using System.Threading.Channels;

namespace PasswordManagerLocal.Windows.Tests.IPC.Infrastructure;

internal sealed class ServerRecordingWindowsIpcConnection : IWindowsIpcConnection
{
    private readonly Channel<IpcFrame> _incoming = Channel.CreateUnbounded<IpcFrame>();
    private readonly SemaphoreSlim _writeLock = new(1, 1);
    private readonly TaskCompletionSource _releaseResponseWrites = new(
        TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly object _writesGate = new();
    private readonly List<IpcFrame> _startedWrites = new();
    private readonly List<IpcFrame> _completedWrites = new();
    private TaskCompletionSource _responseWriteCallsChanged = NewSignal();
    private TaskCompletionSource _startedWritesChanged = NewSignal();
    private CancellationToken _lastResponseWriteToken;
    private int _responseWriteCallCount;
    private int _disposeStarted;

    public ServerRecordingWindowsIpcConnection(bool blockResponseWrites = false)
    {
        BlockResponseWrites = blockResponseWrites;
        ConnectionId = Guid.NewGuid();
    }

    public Guid ConnectionId { get; }
    public int? VerifiedPeerProcessId => null;
    public bool IsConnected => Volatile.Read(ref _disposeStarted) == 0;
    public bool BlockResponseWrites { get; set; }
    public int ResponseWriteCallCount => Volatile.Read(ref _responseWriteCallCount);

    public CancellationToken LastResponseWriteToken
    {
        get
        {
            lock (_writesGate)
                return _lastResponseWriteToken;
        }
    }

    public IReadOnlyList<IpcFrame> StartedWrites
    {
        get
        {
            lock (_writesGate)
                return _startedWrites.ToArray();
        }
    }

    public IReadOnlyList<IpcFrame> CompletedWrites
    {
        get
        {
            lock (_writesGate)
                return _completedWrites.ToArray();
        }
    }

    public async ValueTask<IpcFrame?> ReadFrameAsync(
        CancellationToken cancellationToken = default)
    {
        try
        {
            return await _incoming.Reader.ReadAsync(cancellationToken);
        }
        catch (ChannelClosedException)
        {
            return null;
        }
    }

    public async ValueTask WriteFrameAsync(
        IpcFrame frame,
        CancellationToken cancellationToken = default)
    {
        RecordWriteCall(frame);
        await _writeLock.WaitAsync(cancellationToken);
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            ThrowIfDisposed();
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
        RecordWriteCall(frame);
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

            await WriteFrameUnderLockAsync(frame, shutdownCancellationToken);
            return IpcFrameWriteResult.Written;
        }
        finally
        {
            _writeLock.Release();
        }
    }

    public void QueueIncoming(IpcFrame frame)
    {
        ArgumentNullException.ThrowIfNull(frame);
        _incoming.Writer.TryWrite(frame);
    }

    public void CompleteIncoming() => _incoming.Writer.TryComplete();

    public void ReleaseResponseWrites() => _releaseResponseWrites.TrySetResult();

    public async Task WaitForResponseWriteCallsAsync(
        int count,
        CancellationToken cancellationToken = default)
    {
        while (true)
        {
            Task waitTask;
            lock (_writesGate)
            {
                if (_responseWriteCallCount >= count)
                    return;

                waitTask = _responseWriteCallsChanged.Task;
            }

            await waitTask.WaitAsync(cancellationToken);
        }
    }

    public async Task WaitForStartedWritesAsync(
        int count,
        CancellationToken cancellationToken = default)
    {
        while (true)
        {
            Task waitTask;
            lock (_writesGate)
            {
                if (_startedWrites.Count >= count)
                    return;

                waitTask = _startedWritesChanged.Task;
            }

            await waitTask.WaitAsync(cancellationToken);
        }
    }

    public ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposeStarted, 1) == 0)
            _incoming.Writer.TryComplete();

        return ValueTask.CompletedTask;
    }

    private async ValueTask WriteFrameUnderLockAsync(
        IpcFrame frame,
        CancellationToken cancellationToken)
    {
        TaskCompletionSource signal;
        lock (_writesGate)
        {
            _startedWrites.Add(frame);
            if (frame.Header.MessageKind == IpcMessageKind.Response)
                _lastResponseWriteToken = cancellationToken;
            signal = _startedWritesChanged;
            _startedWritesChanged = NewSignal();
        }
        signal.TrySetResult();

        if (frame.Header.MessageKind == IpcMessageKind.Response && BlockResponseWrites)
            await _releaseResponseWrites.Task.WaitAsync(cancellationToken);

        lock (_writesGate)
            _completedWrites.Add(frame);
    }

    private void RecordWriteCall(IpcFrame frame)
    {
        if (frame.Header.MessageKind != IpcMessageKind.Response)
            return;

        TaskCompletionSource signal;
        lock (_writesGate)
        {
            _responseWriteCallCount++;
            signal = _responseWriteCallsChanged;
            _responseWriteCallsChanged = NewSignal();
        }
        signal.TrySetResult();
    }

    private void ThrowIfDisposed()
    {
        if (Volatile.Read(ref _disposeStarted) != 0)
            throw new ObjectDisposedException(nameof(ServerRecordingWindowsIpcConnection));
    }

    private static TaskCompletionSource NewSignal() =>
        new(TaskCreationOptions.RunContinuationsAsynchronously);
}
