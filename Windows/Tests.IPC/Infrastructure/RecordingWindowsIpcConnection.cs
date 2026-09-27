using PasswordManagerLocal.Windows.Ipc.Contracts;
using PasswordManagerLocal.Windows.Ipc.Protocol;
using PasswordManagerLocal.Windows.Ipc.Serialization;
using PasswordManagerLocal.Windows.Ipc.Transport;
using System.Threading.Channels;

namespace PasswordManagerLocal.Windows.Tests.IPC.Infrastructure;

internal sealed class RecordingWindowsIpcConnection : IWindowsIpcConnection
{
    private readonly WindowsIpcSerializer _serializer;
    private readonly Channel<IpcFrame> _readFrames = Channel.CreateUnbounded<IpcFrame>();
    private readonly SemaphoreSlim _writeLock = new(1, 1);
    private readonly TaskCompletionSource _releaseRequestWrites = new(
        TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource _releaseCancellationWrites = new(
        TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource _releaseRequestWriteReturns = new(
        TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly object _readsGate = new();
    private readonly object _writesGate = new();
    private readonly List<IpcFrame> _startedWrites = new();
    private readonly List<IpcFrame> _completedWrites = new();
    private TaskCompletionSource _readCallsChanged = NewSignal();
    private TaskCompletionSource _startedWritesChanged = NewSignal();
    private TaskCompletionSource _completedWritesChanged = NewSignal();
    private TaskCompletionSource _admissionAttemptsChanged = NewSignal();
    private CancellationToken _lastRequestWriteToken;
    private int _readCount;
    private int _admissionAttemptCount;
    private int _skippedWriteCount;
    private int _disposeStarted;

    public RecordingWindowsIpcConnection(
        WindowsIpcSerializer serializer,
        bool blockRequestWrites = false,
        bool blockCancellationWrites = false,
        bool blockRequestWriteReturns = false)
    {
        _serializer = serializer ?? throw new ArgumentNullException(nameof(serializer));
        BlockRequestWrites = blockRequestWrites;
        BlockCancellationWrites = blockCancellationWrites;
        BlockRequestWriteReturns = blockRequestWriteReturns;
        ConnectionId = Guid.NewGuid();
    }

    public Guid ConnectionId { get; }
    public int? VerifiedPeerProcessId => null;
    public bool IsConnected => Volatile.Read(ref _disposeStarted) == 0;
    public bool BlockRequestWrites { get; set; }
    public bool BlockCancellationWrites { get; set; }
    public bool BlockRequestWriteReturns { get; set; }
    public bool FailRequestWrites { get; set; }
    public bool FailCancellationWrites { get; set; }
    public int AdmissionAttemptCount => Volatile.Read(ref _admissionAttemptCount);
    public int SkippedWriteCount => Volatile.Read(ref _skippedWriteCount);

    public CancellationToken LastRequestWriteToken
    {
        get
        {
            lock (_writesGate)
                return _lastRequestWriteToken;
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

    public ValueTask<IpcFrame?> ReadFrameAsync(CancellationToken cancellationToken = default)
    {
        var readCount = RecordReadCall();
        if (readCount == 1)
            return new ValueTask<IpcFrame?>(CreateHandshakeResponse());

        return ReadQueuedFrameAsync(cancellationToken);
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
            RecordAdmissionAttempt(admitted: false);
            return IpcFrameWriteResult.SkippedBeforeTransmission;
        }

        try
        {
            ThrowIfDisposed();
            shutdownCancellationToken.ThrowIfCancellationRequested();
            var admitted = tryBeginWrite();
            RecordAdmissionAttempt(admitted);
            if (!admitted)
                return IpcFrameWriteResult.SkippedBeforeTransmission;

            await WriteFrameUnderLockAsync(frame, shutdownCancellationToken);
            return IpcFrameWriteResult.Written;
        }
        finally
        {
            _writeLock.Release();
        }
    }

    public void ReleaseRequestWrites() => _releaseRequestWrites.TrySetResult();

    public void ReleaseCancellationWrites() => _releaseCancellationWrites.TrySetResult();

    public void ReleaseRequestWriteReturns() => _releaseRequestWriteReturns.TrySetResult();

    public void QueueSuccessResponse(long correlationId)
    {
        var response = IpcResponseEnvelope.Success(correlationId);
        var payload = _serializer.Serialize(
            response,
            WindowsIpcJsonContext.Default.IpcResponseEnvelope);
        _readFrames.Writer.TryWrite(new IpcFrame(
            new IpcFrameHeader(
                WindowsIpcProtocol.CurrentVersion,
                IpcMessageKind.Response,
                IpcFrameFlags.None,
                correlationId,
                payload.Length),
            payload));
    }

    public void QueueResponse(IpcResponseEnvelope response)
    {
        ArgumentNullException.ThrowIfNull(response);
        var payload = _serializer.Serialize(
            response,
            WindowsIpcJsonContext.Default.IpcResponseEnvelope);
        _readFrames.Writer.TryWrite(new IpcFrame(
            new IpcFrameHeader(
                WindowsIpcProtocol.CurrentVersion,
                IpcMessageKind.Response,
                IpcFrameFlags.None,
                response.CorrelationId,
                payload.Length),
            payload));
    }

    public async Task WaitForReadCallsAsync(
        int count,
        CancellationToken cancellationToken = default)
    {
        while (true)
        {
            Task waitTask;
            lock (_readsGate)
            {
                if (_readCount >= count)
                    return;

                waitTask = _readCallsChanged.Task;
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

    public async Task WaitForCompletedWritesAsync(
        int count,
        CancellationToken cancellationToken = default)
    {
        while (true)
        {
            Task waitTask;
            lock (_writesGate)
            {
                if (_completedWrites.Count >= count)
                    return;

                waitTask = _completedWritesChanged.Task;
            }

            await waitTask.WaitAsync(cancellationToken);
        }
    }

    public async Task WaitForAdmissionAttemptsAsync(
        int count,
        CancellationToken cancellationToken = default)
    {
        while (true)
        {
            Task waitTask;
            lock (_writesGate)
            {
                if (_admissionAttemptCount >= count)
                    return;

                waitTask = _admissionAttemptsChanged.Task;
            }

            await waitTask.WaitAsync(cancellationToken);
        }
    }

    public ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposeStarted, 1) == 0)
            _readFrames.Writer.TryComplete();

        return ValueTask.CompletedTask;
    }

    private async ValueTask WriteFrameUnderLockAsync(
        IpcFrame frame,
        CancellationToken cancellationToken)
    {
        RecordStarted(frame, cancellationToken);
        if (frame.Header.MessageKind == IpcMessageKind.Request && BlockRequestWrites)
            await _releaseRequestWrites.Task.WaitAsync(cancellationToken);
        if (frame.Header.MessageKind == IpcMessageKind.RequestCancellation && BlockCancellationWrites)
            await _releaseCancellationWrites.Task.WaitAsync(cancellationToken);
        if (frame.Header.MessageKind == IpcMessageKind.Request && FailRequestWrites)
            throw new IOException("Request write failed.");
        if (frame.Header.MessageKind == IpcMessageKind.RequestCancellation && FailCancellationWrites)
            throw new IOException("Cancellation write failed.");

        RecordCompleted(frame);
        if (frame.Header.MessageKind == IpcMessageKind.Request && BlockRequestWriteReturns)
            await _releaseRequestWriteReturns.Task.WaitAsync(cancellationToken);
    }

    private async ValueTask<IpcFrame?> ReadQueuedFrameAsync(CancellationToken cancellationToken)
    {
        try
        {
            return await _readFrames.Reader.ReadAsync(cancellationToken);
        }
        catch (ChannelClosedException)
        {
            return null;
        }
    }

    private int RecordReadCall()
    {
        TaskCompletionSource signal;
        int readCount;
        lock (_readsGate)
        {
            readCount = ++_readCount;
            signal = _readCallsChanged;
            _readCallsChanged = NewSignal();
        }

        signal.TrySetResult();
        return readCount;
    }

    private void RecordAdmissionAttempt(bool admitted)
    {
        TaskCompletionSource signal;
        lock (_writesGate)
        {
            _admissionAttemptCount++;
            if (!admitted)
                _skippedWriteCount++;
            signal = _admissionAttemptsChanged;
            _admissionAttemptsChanged = NewSignal();
        }

        signal.TrySetResult();
    }

    private void RecordStarted(IpcFrame frame, CancellationToken cancellationToken)
    {
        TaskCompletionSource signal;
        lock (_writesGate)
        {
            _startedWrites.Add(frame);
            if (frame.Header.MessageKind == IpcMessageKind.Request)
                _lastRequestWriteToken = cancellationToken;
            signal = _startedWritesChanged;
            _startedWritesChanged = NewSignal();
        }

        signal.TrySetResult();
    }

    private void RecordCompleted(IpcFrame frame)
    {
        TaskCompletionSource signal;
        lock (_writesGate)
        {
            _completedWrites.Add(frame);
            signal = _completedWritesChanged;
            _completedWritesChanged = NewSignal();
        }

        signal.TrySetResult();
    }

    private IpcFrame CreateHandshakeResponse()
    {
        var response = new IpcHandshakeResponse(
            Accepted: true,
            WindowsIpcProtocol.CurrentVersion,
            IpcPeerRole.Agent,
            ConnectionId,
            IpcCapabilities.Control | IpcCapabilities.Status,
            Error: null);
        var payload = _serializer.Serialize(
            response,
            WindowsIpcJsonContext.Default.IpcHandshakeResponse);
        return new IpcFrame(
            new IpcFrameHeader(
                WindowsIpcProtocol.CurrentVersion,
                IpcMessageKind.HandshakeResponse,
                IpcFrameFlags.None,
                1,
                payload.Length),
            payload);
    }

    private void ThrowIfDisposed()
    {
        if (Volatile.Read(ref _disposeStarted) != 0)
            throw new ObjectDisposedException(nameof(RecordingWindowsIpcConnection));
    }

    private static TaskCompletionSource NewSignal() =>
        new(TaskCreationOptions.RunContinuationsAsynchronously);
}
