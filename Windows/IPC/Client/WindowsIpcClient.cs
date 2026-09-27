using PasswordManagerLocal.Windows.Ipc.Contracts;
using PasswordManagerLocal.Windows.Ipc.Protocol;
using PasswordManagerLocal.Windows.Ipc.Serialization;
using PasswordManagerLocal.Windows.Ipc.Transport;
using PasswordManagerLocal.Windows.Ipc.Validation;
using System.Collections.Concurrent;

namespace PasswordManagerLocal.Windows.Ipc.Client;

public sealed class WindowsIpcClient : IAsyncDisposable
{
    private const long HandshakeCorrelationId = 1;

    private readonly IWindowsIpcConnection _connection;
    private readonly WindowsIpcSerializer _serializer;
    private readonly WindowsIpcClientOptions _options;
    private readonly WindowsIpcContractValidator _contractValidator;
    private readonly Func<IpcRequestEnvelope, byte[]> _serializeRequestEnvelope;
    private readonly Func<long, PendingIpcRequest, bool> _allowPendingRequestRegistration;
    private readonly SemaphoreSlim _handshakeLock = new(1, 1);
    private readonly SemaphoreSlim _pendingRequestCapacity;
    private readonly CancellationTokenSource _shutdownSource = new();
    private readonly CancellationToken _shutdownToken;
    private readonly ConcurrentDictionary<long, PendingIpcRequest> _pendingRequests = new();
    private readonly ConcurrentDictionary<Task, byte> _backgroundWrites = new();
    private readonly object _pendingRegistrationGate = new();
    private readonly object _connectionDisposalGate = new();
    private readonly object _disposeGate = new();
    private readonly object _failureGate = new();
    private Task? _connectionDisposalTask;
    private Task? _disposeTask;
    private Task? _readLoopTask;
    private Exception? _terminationFailure;
    private Exception? _cleanupFailure;
    private long _nextCorrelationId = HandshakeCorrelationId;
    private int _handshakeState;
    private int _disposeStarted;

    public WindowsIpcClient(
        IWindowsIpcConnection connection,
        WindowsIpcSerializer serializer,
        WindowsIpcClientOptions options,
        WindowsIpcContractValidator? contractValidator = null)
        : this(
            connection,
            serializer,
            options,
            contractValidator,
            requestEnvelopeSerializer: null,
            pendingRequestRegistrationGuard: null)
    {
    }

    internal WindowsIpcClient(
        IWindowsIpcConnection connection,
        WindowsIpcSerializer serializer,
        WindowsIpcClientOptions options,
        WindowsIpcContractValidator? contractValidator,
        Func<IpcRequestEnvelope, byte[]>? requestEnvelopeSerializer,
        Func<long, PendingIpcRequest, bool>? pendingRequestRegistrationGuard)
    {
        _connection = connection ?? throw new ArgumentNullException(nameof(connection));
        _serializer = serializer ?? throw new ArgumentNullException(nameof(serializer));
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _contractValidator = contractValidator ?? new WindowsIpcContractValidator();
        _shutdownToken = _shutdownSource.Token;
        _serializeRequestEnvelope = requestEnvelopeSerializer
            ?? (request => _serializer.Serialize(
                request,
                WindowsIpcJsonContext.Default.IpcRequestEnvelope));
        _allowPendingRequestRegistration = pendingRequestRegistrationGuard ?? ((_, _) => true);
        _pendingRequestCapacity = new SemaphoreSlim(
            options.MaximumPendingRequests,
            options.MaximumPendingRequests);
    }

    public Guid? ServerConnectionId { get; private set; }
    public IpcCapabilities ServerCapabilities { get; private set; }
    public bool IsHandshakeComplete => Volatile.Read(ref _handshakeState) == 2;
    public bool IsConnected =>
        IsHandshakeComplete &&
        Volatile.Read(ref _disposeStarted) == 0 &&
        Volatile.Read(ref _readLoopTask) is { IsCompleted: false };
    public int PendingRequestCount => _pendingRequests.Count;
    public Task Completion => Volatile.Read(ref _readLoopTask) ?? Task.CompletedTask;
    public int? VerifiedServerProcessId => _connection.VerifiedPeerProcessId;

    public Exception? TerminationFailure
    {
        get
        {
            lock (_failureGate)
                return _terminationFailure;
        }
    }

    public Exception? CleanupFailure
    {
        get
        {
            lock (_failureGate)
                return _cleanupFailure;
        }
    }

    public async Task HandshakeAsync(CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        using var linkedSource = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken,
            _shutdownToken);
        await _handshakeLock.WaitAsync(linkedSource.Token);
        try
        {
            ThrowIfDisposed();
            if (_handshakeState == 2)
                throw new InvalidOperationException("The IPC handshake has already completed.");
            if (_handshakeState != 0)
                throw new InvalidOperationException("The IPC connection cannot start another handshake.");

            _handshakeState = 1;
            try
            {
                var request = new IpcHandshakeRequest(
                    WindowsIpcProtocol.CurrentVersion,
                    _options.ClientRole,
                    _options.ProcessId,
                    _options.WindowsSessionId,
                    _options.SessionId,
                    _options.Capabilities);
                _contractValidator.Validate(request);
                var payload = _serializer.Serialize(
                    request,
                    WindowsIpcJsonContext.Default.IpcHandshakeRequest);
                await _connection.WriteFrameAsync(
                    CreateFrame(IpcMessageKind.HandshakeRequest, HandshakeCorrelationId, payload),
                    linkedSource.Token);

                var frame = await _connection.ReadFrameAsync(linkedSource.Token)
                    ?? throw new IpcConnectionClosedException(
                        "The IPC connection closed during the handshake.");
                ValidateFrame(frame);
                if (frame.Header.MessageKind != IpcMessageKind.HandshakeResponse ||
                    frame.Header.CorrelationId != HandshakeCorrelationId)
                {
                    throw new IpcProtocolException(
                        IpcProtocolErrorCode.UnexpectedMessageKind,
                        "The IPC handshake response is invalid.");
                }

                var response = _serializer.Deserialize(
                    frame.Payload,
                    WindowsIpcJsonContext.Default.IpcHandshakeResponse);
                ValidateHandshakeResponse(response);
                if (!response.Accepted)
                    throw new IpcRemoteException(response.Error!);

                ThrowIfDisposed();
                ServerConnectionId = response.ConnectionId;
                ServerCapabilities = response.Capabilities;
                Volatile.Write(ref _handshakeState, 2);
                _readLoopTask = RunReadLoopAsync();
            }
            catch (Exception exception)
            {
                Volatile.Write(ref _handshakeState, 3);
                var disposalFailure = await CaptureConnectionDisposalFailureAsync();
                if (disposalFailure is not null)
                    RecordCleanupFailure(disposalFailure);

                var failure = CombineFailures(exception, disposalFailure);
                RecordTerminationFailure(failure);
                throw failure;
            }
        }
        finally
        {
            _handshakeLock.Release();
        }
    }

    public async Task<IpcResponseEnvelope> SendAsync(
        IpcOperationId operationId,
        byte[]? payload = null,
        CancellationToken cancellationToken = default)
    {
        try
        {
            var result = await SendWithTransmissionStateAsync(
                operationId,
                payload,
                cancellationToken);
            return result.Response;
        }
        catch (IpcRequestTransmissionException exception)
        {
            System.Runtime.ExceptionServices.ExceptionDispatchInfo
                .Capture(exception.InnerException ?? exception)
                .Throw();
            throw;
        }
    }

    public async Task<IpcTransmissionAwareResponse> SendWithTransmissionStateAsync(
        IpcOperationId operationId,
        byte[]? payload = null,
        CancellationToken cancellationToken = default)
    {
        long correlationId = 0;
        try
        {
            ThrowIfDisposed();
            if (!IsHandshakeComplete)
                throw new InvalidOperationException("The IPC handshake has not completed.");
            if (!Enum.IsDefined(operationId))
                throw new ArgumentOutOfRangeException(nameof(operationId));

            correlationId = GetNextCorrelationId();
            cancellationToken.ThrowIfCancellationRequested();
            var request = new IpcRequestEnvelope(correlationId, operationId, payload);
            _contractValidator.Validate(request);
            await WaitForPendingRequestCapacityAsync(cancellationToken);

            var pending = new PendingIpcRequest(
                correlationId,
                cancellationToken,
                ReleasePendingRequest);
            try
            {
                byte[]? serialized = null;
                if (cancellationToken.IsCancellationRequested)
                {
                    pending.RequestCallerCancellation();
                }
                else
                {
                    try
                    {
                        ThrowIfDisposed();
                        if (!IsHandshakeComplete)
                        {
                            throw new IpcConnectionClosedException(
                                "The IPC connection is not available.");
                        }

                        serialized = _serializeRequestEnvelope(request);
                        _contractValidator.ValidateSerializedRequestEnvelope(
                            correlationId,
                            serialized);
                    }
                    catch (Exception exception)
                    {
                        if (cancellationToken.IsCancellationRequested)
                            pending.RequestCallerCancellation();
                        else
                            pending.TrySetException(exception);
                    }
                }

                if (serialized is not null &&
                    pending.SubmissionState != IpcRequestSubmissionState.Completed)
                {
                    pending.RegisterCallerCancellation(
                        () => CancelPendingRequest(pending));

                    try
                    {
                        lock (_pendingRegistrationGate)
                        {
                            ThrowIfDisposed();
                            if (!IsHandshakeComplete)
                            {
                                throw new IpcConnectionClosedException(
                                    "The IPC connection is not available.");
                            }

                            var registered = pending.TryRegisterPending(
                                () =>
                                    _allowPendingRequestRegistration(correlationId, pending) &&
                                    _pendingRequests.TryAdd(correlationId, pending));
                            if (registered && pending.TryQueue())
                            {
                                TrackBackgroundWrite(SubmitRequestAsync(
                                    pending,
                                    CreateFrame(
                                        IpcMessageKind.Request,
                                        correlationId,
                                        serialized)));
                            }
                        }
                    }
                    catch (Exception exception)
                    {
                        pending.TrySetException(exception);
                    }
                }

                IpcResponseEnvelope response;
                try
                {
                    response = await pending.Task;
                }
                catch (Exception exception)
                    when (exception is not IpcRemoteException)
                {
                    throw new IpcRequestTransmissionException(
                        correlationId,
                        pending.TransmissionState,
                        exception);
                }

                if (!response.IsSuccess)
                    throw new IpcRemoteException(response.Error!);

                return new IpcTransmissionAwareResponse(
                    correlationId,
                    IpcRequestTransmissionState.Sent,
                    response);
            }
            finally
            {
                pending.DisposeCancellationRegistration();
            }
        }
        catch (IpcRequestTransmissionException)
        {
            throw;
        }
        catch (IpcRemoteException)
        {
            throw;
        }
        catch (Exception exception)
        {
            throw new IpcRequestTransmissionException(
                correlationId,
                IpcRequestTransmissionState.DefinitelyNotSent,
                exception);
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

    private async Task DisposeCoreAsync()
    {
        lock (_pendingRegistrationGate)
        {
        }

        var cancellationFailure = CaptureCancellationFailure(_shutdownSource);
        if (cancellationFailure is not null)
            RecordCleanupFailure(cancellationFailure);
        var connectionDisposalTask = CaptureConnectionDisposalFailureAsync();

        FailAllPending(new IpcConnectionClosedException("The IPC client was disposed."));

        await _handshakeLock.WaitAsync();
        _handshakeLock.Release();

        if (_readLoopTask is not null)
            await _readLoopTask;

        await AwaitBackgroundWritesAsync();
        var disposalFailure = await connectionDisposalTask;
        if (disposalFailure is not null)
            RecordCleanupFailure(disposalFailure);

        _shutdownSource.Dispose();
        GC.SuppressFinalize(this);

        var cleanupFailure = CleanupFailure;
        if (cleanupFailure is not null)
            throw cleanupFailure;
    }

    private async Task RunReadLoopAsync()
    {
        Exception failure;
        try
        {
            while (true)
            {
                var frame = await _connection.ReadFrameAsync(_shutdownToken);
                if (frame is null)
                {
                    failure = new IpcConnectionClosedException("The IPC connection was closed.");
                    break;
                }

                ValidateFrame(frame);
                if (frame.Header.MessageKind != IpcMessageKind.Response)
                {
                    throw new IpcProtocolException(
                        IpcProtocolErrorCode.UnexpectedMessageKind,
                        "The IPC client received an unexpected message kind.");
                }

                var response = _serializer.Deserialize(
                    frame.Payload,
                    WindowsIpcJsonContext.Default.IpcResponseEnvelope);
                ValidateResponse(frame.Header, response);
                if (_pendingRequests.TryGetValue(response.CorrelationId, out var pending))
                    pending.TrySetResult(response);
            }
        }
        catch (OperationCanceledException) when (_shutdownSource.IsCancellationRequested)
        {
            failure = new IpcConnectionClosedException("The IPC client was stopped.");
        }
        catch (Exception exception)
        {
            failure = new IpcConnectionClosedException(
                "The IPC connection failed.",
                exception);
        }

        Volatile.Write(ref _handshakeState, 3);
        lock (_pendingRegistrationGate)
        {
        }

        FailAllPending(failure);
        var cancellationFailure = CaptureCancellationFailure(_shutdownSource);
        if (cancellationFailure is not null)
            RecordCleanupFailure(cancellationFailure);
        var connectionDisposalTask = CaptureConnectionDisposalFailureAsync();

        var disposalFailure = await connectionDisposalTask;
        if (disposalFailure is not null)
            RecordCleanupFailure(disposalFailure);
        await AwaitBackgroundWritesAsync();

        var terminalFailure = CombineFailures(failure, cancellationFailure);
        terminalFailure = CombineFailures(terminalFailure, disposalFailure);
        if (Volatile.Read(ref _disposeStarted) == 0)
            RecordTerminationFailure(terminalFailure);
    }

    private void CancelPendingRequest(PendingIpcRequest pending)
    {
        if (pending.RequestCallerCancellation())
            TrackBackgroundWrite(SendCancellationBestEffortAsync(pending.CorrelationId));
    }

    private async Task SubmitRequestAsync(
        PendingIpcRequest pending,
        IpcFrame frame)
    {
        try
        {
            var writeResult = await _connection.TryWriteFrameAsync(
                frame,
                pending.TryBeginSending,
                pending.QueuedCancellationToken,
                _shutdownToken);
            if (writeResult == IpcFrameWriteResult.Written && pending.CommitSent())
            {
                TrackBackgroundWrite(
                    SendCancellationBestEffortAsync(pending.CorrelationId));
            }
        }
        catch (OperationCanceledException exception)
            when (_shutdownSource.IsCancellationRequested)
        {
            pending.TrySetException(new IpcConnectionClosedException(
                "The IPC request write was interrupted by client shutdown.",
                exception));
        }
        catch (Exception exception)
        {
            pending.TrySetException(exception);
        }
    }

    private async Task SendCancellationBestEffortAsync(long correlationId)
    {
        try
        {
            if (Volatile.Read(ref _disposeStarted) == 0 && IsHandshakeComplete)
            {
                await _connection.WriteFrameAsync(
                    CreateFrame(IpcMessageKind.RequestCancellation, correlationId, Array.Empty<byte>()),
                    _shutdownToken);
            }
        }
        catch
        {
        }
    }

    private async Task WaitForPendingRequestCapacityAsync(
        CancellationToken callerCancellationToken)
    {
        using var linkedSource = CancellationTokenSource.CreateLinkedTokenSource(
            callerCancellationToken,
            _shutdownToken);
        try
        {
            await _pendingRequestCapacity.WaitAsync(linkedSource.Token);
        }
        catch (OperationCanceledException)
            when (callerCancellationToken.IsCancellationRequested)
        {
            throw new OperationCanceledException(callerCancellationToken);
        }
        catch (OperationCanceledException exception)
            when (_shutdownSource.IsCancellationRequested)
        {
            throw new IpcConnectionClosedException(
                "The IPC client stopped while waiting for request capacity.",
                exception);
        }
    }

    private void TrackBackgroundWrite(Task task)
    {
        ArgumentNullException.ThrowIfNull(task);
        if (!_backgroundWrites.TryAdd(task, 0))
            return;

        _ = task.ContinueWith(
            completedTask => _backgroundWrites.TryRemove(completedTask, out _),
            CancellationToken.None,
            TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);
    }

    private async Task AwaitBackgroundWritesAsync()
    {
        while (_backgroundWrites.Count > 0)
        {
            var tasks = _backgroundWrites.Keys.ToArray();
            if (tasks.Length == 0)
            {
                await Task.Yield();
                continue;
            }

            await Task.WhenAll(tasks);
        }
    }

    private void ReleasePendingRequest(PendingIpcRequest expected)
    {
        var pair = new KeyValuePair<long, PendingIpcRequest>(
            expected.CorrelationId,
            expected);
        ((ICollection<KeyValuePair<long, PendingIpcRequest>>)_pendingRequests).Remove(pair);
        _pendingRequestCapacity.Release();
    }

    private void FailAllPending(Exception exception)
    {
        foreach (var pending in _pendingRequests.Values.ToArray())
            pending.TrySetException(exception);
    }

    private Task DisposeConnectionOnceAsync()
    {
        lock (_connectionDisposalGate)
        {
            return _connectionDisposalTask ??= _connection.DisposeAsync().AsTask();
        }
    }

    private async Task<Exception?> CaptureConnectionDisposalFailureAsync()
    {
        try
        {
            await DisposeConnectionOnceAsync();
            return null;
        }
        catch (Exception exception)
        {
            return exception;
        }
    }

    private void ValidateHandshakeResponse(IpcHandshakeResponse response)
    {
        try
        {
            _contractValidator.Validate(response);
        }
        catch (IpcPayloadException exception)
        {
            throw new IpcProtocolException(
                IpcProtocolErrorCode.InvalidEnvelope,
                "The IPC handshake response is invalid.",
                exception);
        }

        if (response.ProtocolVersion != WindowsIpcProtocol.CurrentVersion ||
            response.ServerRole != _options.ExpectedServerRole ||
            (response.Capabilities & _options.RequiredServerCapabilities) !=
                _options.RequiredServerCapabilities ||
            (response.Error is not null &&
                response.Error.CorrelationId != HandshakeCorrelationId))
        {
            throw new IpcProtocolException(
                IpcProtocolErrorCode.InvalidEnvelope,
                "The IPC handshake response is invalid.");
        }
    }

    private void ValidateResponse(
        IpcFrameHeader header,
        IpcResponseEnvelope response)
    {
        try
        {
            _contractValidator.Validate(response);
        }
        catch (IpcPayloadException exception)
        {
            throw new IpcProtocolException(
                IpcProtocolErrorCode.InvalidEnvelope,
                "The IPC response envelope is invalid.",
                exception);
        }

        if (response.CorrelationId != header.CorrelationId)
        {
            throw new IpcProtocolException(
                IpcProtocolErrorCode.InvalidEnvelope,
                "The IPC response envelope is invalid.");
        }
    }

    private long GetNextCorrelationId()
    {
        var correlationId = Interlocked.Increment(ref _nextCorrelationId);
        if (correlationId <= 0)
            throw new InvalidOperationException("The IPC correlation ID space is exhausted.");

        return correlationId;
    }

    private static void ValidateFrame(IpcFrame frame)
    {
        if (frame.Header.ProtocolVersion != WindowsIpcProtocol.CurrentVersion ||
            !Enum.IsDefined(frame.Header.MessageKind) ||
            frame.Header.Flags != IpcFrameFlags.None ||
            frame.Header.CorrelationId <= 0 ||
            frame.Header.PayloadLength != frame.Payload.Length ||
            frame.Payload.Length > WindowsIpcProtocol.MaximumPayloadSize)
        {
            throw new IpcProtocolException(
                IpcProtocolErrorCode.InvalidEnvelope,
                "The IPC frame is invalid.");
        }
    }

    private static IpcFrame CreateFrame(
        IpcMessageKind messageKind,
        long correlationId,
        byte[] payload) =>
        new(
            new IpcFrameHeader(
                WindowsIpcProtocol.CurrentVersion,
                messageKind,
                IpcFrameFlags.None,
                correlationId,
                payload.Length),
            payload);

    private static Exception? CaptureCancellationFailure(CancellationTokenSource source)
    {
        try
        {
            source.Cancel();
            return null;
        }
        catch (Exception exception)
        {
            return exception;
        }
    }

    private void RecordTerminationFailure(Exception exception)
    {
        lock (_failureGate)
            _terminationFailure = CombineFailures(_terminationFailure, exception);
    }

    private void RecordCleanupFailure(Exception exception)
    {
        lock (_failureGate)
            _cleanupFailure = CombineFailures(_cleanupFailure, exception);
    }

    private static Exception CombineFailures(Exception? first, Exception? second)
    {
        if (first is null)
            return second ?? throw new ArgumentNullException(nameof(second));
        if (second is null || ReferenceEquals(first, second))
            return first;

        return new AggregateException(first, second);
    }

    private void ThrowIfDisposed()
    {
        if (Volatile.Read(ref _disposeStarted) != 0)
            throw new ObjectDisposedException(nameof(WindowsIpcClient));
    }
}
