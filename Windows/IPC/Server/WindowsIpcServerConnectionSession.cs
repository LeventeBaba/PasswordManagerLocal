using PasswordManagerLocal.Windows.Ipc.Contracts;
using PasswordManagerLocal.Windows.Ipc.Lifecycle;
using PasswordManagerLocal.Windows.Ipc.Protocol;
using PasswordManagerLocal.Windows.Ipc.Serialization;
using PasswordManagerLocal.Windows.Ipc.Transport;
using PasswordManagerLocal.Windows.Ipc.Validation;
using System.Collections.Concurrent;

namespace PasswordManagerLocal.Windows.Ipc.Server;

public sealed class WindowsIpcServerConnectionSession : IWindowsIpcServerSession
{
    private const int StopKindNone = 0;
    private const int StopKindRequested = 1;
    private const int StopKindRequestFailure = 2;

    private readonly IWindowsIpcConnection _connection;
    private readonly WindowsIpcSerializer _serializer;
    private readonly WindowsIpcRequestDispatcher _dispatcher;
    private readonly WindowsIpcServerOptions _options;
    private readonly WindowsIpcContractValidator _contractValidator;
    private readonly IReadOnlyList<IWindowsIpcConnectionLifecycleObserver> _observers;
    private readonly IUiConnectionCoordinator? _uiConnectionCoordinator;
    private readonly IWindowsIpcHandshakeAuthorizer? _handshakeAuthorizer;
    private readonly SemaphoreSlim _activeRequestCapacity;
    private readonly CancellationTokenSource _shutdownSource = new();
    private readonly ConcurrentDictionary<long, ServerIpcRequestExecution> _activeRequests = new();
    private readonly ConcurrentQueue<Exception> _backgroundFailures = new();
    private readonly ConcurrentQueue<Exception> _backgroundCleanupFailures = new();
    private readonly object _runGate = new();
    private readonly object _disposeGate = new();
    private readonly object _connectionDisposalGate = new();
    private readonly object _failureGate = new();
    private Task? _runTask;
    private Task? _disposeTask;
    private Task? _connectionDisposalTask;
    private Exception? _terminationFailure;
    private Exception? _cleanupFailure;
    private int _connectionEnding;
    private int _stopKind;
    private int _disposeStarted;

    public WindowsIpcServerConnectionSession(
        IWindowsIpcConnection connection,
        WindowsIpcSerializer serializer,
        WindowsIpcRequestDispatcher dispatcher,
        WindowsIpcServerOptions options,
        IEnumerable<IWindowsIpcConnectionLifecycleObserver>? observers = null,
        IUiConnectionCoordinator? uiConnectionCoordinator = null,
        WindowsIpcContractValidator? contractValidator = null,
        IWindowsIpcHandshakeAuthorizer? handshakeAuthorizer = null)
    {
        _connection = connection ?? throw new ArgumentNullException(nameof(connection));
        _serializer = serializer ?? throw new ArgumentNullException(nameof(serializer));
        _dispatcher = dispatcher ?? throw new ArgumentNullException(nameof(dispatcher));
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _contractValidator = contractValidator ?? new WindowsIpcContractValidator();
        _observers = observers?.ToArray()
            ?? Array.Empty<IWindowsIpcConnectionLifecycleObserver>();
        if (options.ManagesUiRegistration &&
            options.AcceptedClientRoles.Contains(IpcPeerRole.Ui) &&
            uiConnectionCoordinator is null)
        {
            throw new ArgumentNullException(
                nameof(uiConnectionCoordinator),
                "A UI connection coordinator is required when the server accepts UI clients.");
        }

        _uiConnectionCoordinator = uiConnectionCoordinator;
        _handshakeAuthorizer = handshakeAuthorizer;
        _activeRequestCapacity = new SemaphoreSlim(
            options.MaximumActiveRequestsPerConnection,
            options.MaximumActiveRequestsPerConnection);
    }

    public int ActiveRequestCount => _activeRequests.Count;

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

    public Task RunAsync(CancellationToken cancellationToken = default)
    {
        lock (_runGate)
        {
            if (_runTask is not null)
                throw new InvalidOperationException("The IPC server connection session has already started.");
            if (Volatile.Read(ref _disposeStarted) != 0)
                throw new ObjectDisposedException(nameof(WindowsIpcServerConnectionSession));

            _runTask = RunCoreAsync(cancellationToken);
            return _runTask;
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

    private async Task RunCoreAsync(CancellationToken cancellationToken)
    {
        IpcConnectionContext? context = null;
        var lifecycleState = IpcConnectionLifecycleState.Disconnected;
        var disconnectKind = IpcDisconnectKind.Clean;
        Exception? primaryFailure = null;
        var cleanupFailures = new List<Exception>();
        using var connectionLifetimeSource = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken,
            _shutdownSource.Token);
        var connectionToken = connectionLifetimeSource.Token;

        AddFailure(
            cleanupFailures,
            await NotifyAsync(
                new IpcConnectionLifecycleNotification(
                    _connection.ConnectionId,
                    IpcConnectionLifecycleState.Connected,
                    null,
                    IpcDisconnectKind.None,
                    DateTimeOffset.UtcNow),
                connectionToken));

        try
        {
            context = await PerformHandshakeAsync(connectionToken);
            if (context is null)
                return;

            AddFailure(
                cleanupFailures,
                await NotifyAsync(
                    new IpcConnectionLifecycleNotification(
                        _connection.ConnectionId,
                        IpcConnectionLifecycleState.HandshakeCompleted,
                        context,
                        IpcDisconnectKind.None,
                        DateTimeOffset.UtcNow),
                    connectionToken));

            while (true)
            {
                var frame = await _connection.ReadFrameAsync(connectionToken);
                if (frame is null)
                    break;

                ValidateFrame(frame);
                switch (frame.Header.MessageKind)
                {
                    case IpcMessageKind.Request:
                        await StartRequestAsync(context, frame, connectionToken);
                        break;
                    case IpcMessageKind.RequestCancellation:
                        await ProcessRequestCancellationAsync(frame);
                        break;
                    case IpcMessageKind.HandshakeRequest:
                        await SendDuplicateHandshakeRejectionAsync(
                            frame.Header.CorrelationId,
                            connectionToken);
                        throw new IpcProtocolException(
                            IpcProtocolErrorCode.UnexpectedMessageKind,
                            "A duplicate IPC handshake was received.");
                    default:
                        throw new IpcProtocolException(
                            IpcProtocolErrorCode.UnexpectedMessageKind,
                            "The IPC server received an unexpected message kind.");
                }
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            disconnectKind = IpcDisconnectKind.ServerCancellation;
        }
        catch (OperationCanceledException exception) when (_shutdownSource.IsCancellationRequested)
        {
            if (Volatile.Read(ref _stopKind) == StopKindRequestFailure)
            {
                lifecycleState = IpcConnectionLifecycleState.Faulted;
                disconnectKind = IpcDisconnectKind.TransportFailure;
                primaryFailure = GetFirstBackgroundFailure() ?? exception;
            }
            else
            {
                disconnectKind = IpcDisconnectKind.ServerCancellation;
            }
        }
        catch (IpcProtocolException exception)
        {
            lifecycleState = IpcConnectionLifecycleState.Faulted;
            disconnectKind = IpcDisconnectKind.ProtocolFailure;
            primaryFailure = exception;
        }
        catch (Exception exception)
        {
            lifecycleState = IpcConnectionLifecycleState.Faulted;
            disconnectKind = IpcDisconnectKind.TransportFailure;
            primaryFailure = exception;
        }
        finally
        {
            Interlocked.Exchange(ref _connectionEnding, 1);
            AddFailure(cleanupFailures, CaptureCancellationFailure(connectionLifetimeSource));
            CancelActiveRequests(cleanupFailures);

            var connectionDisposalTask = CaptureConnectionDisposalFailureAsync();
            AddFailures(cleanupFailures, await AwaitActiveRequestsAsync());

            AddFailure(
                cleanupFailures,
                await NotifyAsync(
                    new IpcConnectionLifecycleNotification(
                        _connection.ConnectionId,
                        lifecycleState,
                        context,
                        disconnectKind,
                        DateTimeOffset.UtcNow),
                    CancellationToken.None));

            if (context?.PeerRole == IpcPeerRole.Ui && _uiConnectionCoordinator is not null)
            {
                try
                {
                    _uiConnectionCoordinator.Unregister(context.ConnectionId);
                }
                catch (Exception exception)
                {
                    cleanupFailures.Add(exception);
                }
            }

            AddFailure(cleanupFailures, await connectionDisposalTask);

            while (_backgroundFailures.TryDequeue(out var backgroundFailure))
                primaryFailure = CombineFailures(primaryFailure, backgroundFailure);
            while (_backgroundCleanupFailures.TryDequeue(out var cleanupFailure))
                AddFailure(cleanupFailures, cleanupFailure);

            var combinedCleanupFailure = CombineFailures(cleanupFailures);
            if (combinedCleanupFailure is not null)
                RecordCleanupFailure(combinedCleanupFailure);

            var combinedTerminationFailure = CombineFailures(
                primaryFailure,
                combinedCleanupFailure);
            if (combinedTerminationFailure is not null)
                RecordTerminationFailure(combinedTerminationFailure);
        }
    }

    private async Task DisposeCoreAsync()
    {
        Interlocked.CompareExchange(
            ref _stopKind,
            StopKindRequested,
            StopKindNone);
        var cancellationFailure = CaptureCancellationFailure(_shutdownSource);

        Task? runTask;
        lock (_runGate)
            runTask = _runTask;

        if (cancellationFailure is not null)
        {
            if (runTask is null)
            {
                RecordCleanupFailure(cancellationFailure);
                RecordTerminationFailure(cancellationFailure);
            }
            else
            {
                _backgroundCleanupFailures.Enqueue(cancellationFailure);
            }
        }

        var connectionDisposalTask = CaptureConnectionDisposalFailureAsync();
        if (runTask is null)
        {
            var disposalFailure = await connectionDisposalTask;
            if (disposalFailure is not null)
            {
                RecordCleanupFailure(disposalFailure);
                RecordTerminationFailure(disposalFailure);
            }
        }
        else
        {
            await runTask;
        }

        _shutdownSource.Dispose();
        GC.SuppressFinalize(this);

        var cleanupFailure = CleanupFailure;
        if (cleanupFailure is not null)
            throw cleanupFailure;
    }

    private async Task<IpcConnectionContext?> PerformHandshakeAsync(
        CancellationToken cancellationToken)
    {
        var frame = await _connection.ReadFrameAsync(cancellationToken);
        if (frame is null)
            return null;

        ValidateFrame(frame);
        if (frame.Header.MessageKind != IpcMessageKind.HandshakeRequest)
        {
            await SendResponseAsync(
                Failure(
                    frame.Header.CorrelationId,
                    IpcErrorCode.HandshakeRequired,
                    IpcErrorCategory.Protocol,
                    "An IPC handshake is required before ordinary operations.",
                    isRetryable: false),
                cancellationToken);
            return null;
        }

        IpcHandshakeRequest request;
        try
        {
            request = _serializer.Deserialize(
                frame.Payload,
                WindowsIpcJsonContext.Default.IpcHandshakeRequest);
            _contractValidator.Validate(request);
        }
        catch (IpcPayloadException)
        {
            await SendHandshakeRejectionAsync(
                frame.Header.CorrelationId,
                IpcErrorCode.InvalidPayload,
                "The IPC handshake payload is invalid.",
                cancellationToken);
            return null;
        }

        if (request.ProtocolVersion != WindowsIpcProtocol.CurrentVersion)
        {
            await SendHandshakeRejectionAsync(
                frame.Header.CorrelationId,
                IpcErrorCode.UnsupportedProtocolVersion,
                "The IPC protocol version is not supported.",
                cancellationToken);
            return null;
        }

        if (_connection.VerifiedPeerProcessId is { } verifiedPeerProcessId)
        {
            if (verifiedPeerProcessId != request.ProcessId)
            {
                await SendHandshakeRejectionAsync(
                    frame.Header.CorrelationId,
                    IpcErrorCode.RequestRejected,
                    "The IPC peer process identity could not be verified.",
                    cancellationToken);
                return null;
            }

            int verifiedWindowsSessionId;
            try
            {
                using var peerProcess = System.Diagnostics.Process.GetProcessById(verifiedPeerProcessId);
                verifiedWindowsSessionId = peerProcess.SessionId;
            }
            catch
            {
                await SendHandshakeRejectionAsync(
                    frame.Header.CorrelationId,
                    IpcErrorCode.RequestRejected,
                    "The IPC peer Windows session could not be verified.",
                    cancellationToken);
                return null;
            }

            if (verifiedWindowsSessionId != request.WindowsSessionId)
            {
                await SendHandshakeRejectionAsync(
                    frame.Header.CorrelationId,
                    IpcErrorCode.RequestRejected,
                    "The IPC peer Windows session does not match the connected process.",
                    cancellationToken);
                return null;
            }
        }

        if (!_options.AcceptedClientRoles.Contains(request.ClientRole))
        {
            await SendHandshakeRejectionAsync(
                frame.Header.CorrelationId,
                IpcErrorCode.UnexpectedPeerRole,
                "The IPC client role is not accepted by this server.",
                cancellationToken);
            return null;
        }

        if ((request.Capabilities & ~_options.Capabilities) != 0)
        {
            await SendHandshakeRejectionAsync(
                frame.Header.CorrelationId,
                IpcErrorCode.UnsupportedCapability,
                "The IPC server does not support a requested capability.",
                cancellationToken);
            return null;
        }

        if ((request.Capabilities & _options.RequiredClientCapabilities) !=
            _options.RequiredClientCapabilities)
        {
            await SendHandshakeRejectionAsync(
                frame.Header.CorrelationId,
                IpcErrorCode.UnsupportedCapability,
                "The IPC client does not support a required capability.",
                cancellationToken);
            return null;
        }

        var connectionContext = new IpcConnectionContext(
            _connection.ConnectionId,
            request.ClientRole,
            request.ProcessId,
            request.WindowsSessionId,
            request.SessionId,
            request.Capabilities);

        if (_handshakeAuthorizer is not null)
        {
            var decision = _handshakeAuthorizer.Authorize(connectionContext);
            if (!decision.IsAuthorized)
            {
                await SendHandshakeRejectionAsync(
                    frame.Header.CorrelationId,
                    decision.ErrorCode,
                    decision.SafeMessage,
                    cancellationToken,
                    decision.IsRetryable);
                return null;
            }
        }

        var response = new IpcHandshakeResponse(
            Accepted: true,
            WindowsIpcProtocol.CurrentVersion,
            _options.ServerRole,
            _connection.ConnectionId,
            _options.Capabilities,
            Error: null);
        await SendHandshakeResponseAsync(frame.Header.CorrelationId, response, cancellationToken);

        return connectionContext;
    }

    private async Task StartRequestAsync(
        IpcConnectionContext connectionContext,
        IpcFrame frame,
        CancellationToken connectionCancellationToken)
    {
        IpcRequestEnvelope request;
        try
        {
            request = _serializer.Deserialize(
                frame.Payload,
                WindowsIpcJsonContext.Default.IpcRequestEnvelope);
            ValidateRequest(frame.Header, request);
        }
        catch (IpcPayloadLimitExceededException exception)
            when (exception.ErrorCode == IpcErrorCode.RequestPayloadTooLarge)
        {
            await SendResponseAsync(
                Failure(
                    frame.Header.CorrelationId,
                    exception.ErrorCode,
                    exception.ErrorCategory,
                    exception.SafeMessage,
                    exception.IsRetryable),
                connectionCancellationToken);
            return;
        }
        catch (IpcPayloadException)
        {
            await SendResponseAsync(
                Failure(
                    frame.Header.CorrelationId,
                    IpcErrorCode.InvalidEnvelope,
                    IpcErrorCategory.Validation,
                    "The IPC request envelope is invalid.",
                    isRetryable: false),
                connectionCancellationToken);
            return;
        }
        catch (IpcProtocolException)
        {
            await SendResponseAsync(
                Failure(
                    frame.Header.CorrelationId,
                    IpcErrorCode.InvalidEnvelope,
                    IpcErrorCategory.Protocol,
                    "The IPC request envelope is invalid.",
                    isRetryable: false),
                connectionCancellationToken);
            return;
        }

        if (_activeRequests.ContainsKey(request.CorrelationId))
        {
            await SendResponseAsync(
                Failure(
                    request.CorrelationId,
                    IpcErrorCode.InvalidEnvelope,
                    IpcErrorCategory.Protocol,
                    "The IPC correlation ID is already active.",
                    isRetryable: false),
                connectionCancellationToken);
            return;
        }

        if (!_activeRequestCapacity.Wait(0))
        {
            await SendResponseAsync(
                Failure(
                    request.CorrelationId,
                    IpcErrorCode.TooManyRequests,
                    IpcErrorCategory.Availability,
                    "The IPC server has reached the per-connection request limit.",
                    isRetryable: true),
                connectionCancellationToken);
            return;
        }

        ServerIpcRequestExecution? execution = null;
        var registered = false;
        try
        {
            var cancellationSource = CancellationTokenSource.CreateLinkedTokenSource(
                connectionCancellationToken);
            execution = new ServerIpcRequestExecution(
                cancellationSource,
                () =>
                {
                    _activeRequestCapacity.Release();
                });
            if (!_activeRequests.TryAdd(request.CorrelationId, execution))
            {
                execution.Dispose();
                await SendResponseAsync(
                    Failure(
                        request.CorrelationId,
                        IpcErrorCode.InvalidEnvelope,
                        IpcErrorCategory.Protocol,
                        "The IPC correlation ID is already active.",
                        isRetryable: false),
                    connectionCancellationToken);
                return;
            }

            registered = true;
            execution.Task = ProcessRequestAsync(
                connectionContext,
                request,
                execution,
                connectionCancellationToken);
        }
        catch
        {
            if (registered && execution is not null)
            {
                var pair = new KeyValuePair<long, ServerIpcRequestExecution>(
                    request.CorrelationId,
                    execution);
                ((ICollection<KeyValuePair<long, ServerIpcRequestExecution>>)_activeRequests)
                    .Remove(pair);
                execution.Dispose();
            }
            else if (execution is null)
            {
                _activeRequestCapacity.Release();
            }

            throw;
        }
    }

    private async Task ProcessRequestAsync(
        IpcConnectionContext connectionContext,
        IpcRequestEnvelope request,
        ServerIpcRequestExecution execution,
        CancellationToken connectionCancellationToken)
    {
        try
        {
            var context = new IpcRequestContext(
                connectionContext,
                request,
                _serializer,
                _contractValidator);
            var response = await _dispatcher.DispatchAsync(
                context,
                execution.CancellationSource.Token);

            if (Volatile.Read(ref _connectionEnding) == 0)
                await SendResponseAsync(response, connectionCancellationToken);
        }
        catch (OperationCanceledException) when (execution.CancellationSource.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            _backgroundFailures.Enqueue(exception);
            Interlocked.CompareExchange(
                ref _stopKind,
                StopKindRequestFailure,
                StopKindNone);
            var cancellationFailure = CaptureCancellationFailure(_shutdownSource);
            if (cancellationFailure is not null)
                _backgroundCleanupFailures.Enqueue(cancellationFailure);
            _ = ObserveConnectionDisposalFailureAsync();
        }
        finally
        {
            var pair = new KeyValuePair<long, ServerIpcRequestExecution>(
                request.CorrelationId,
                execution);
            ((ICollection<KeyValuePair<long, ServerIpcRequestExecution>>)_activeRequests)
                .Remove(pair);
            execution.Dispose();
        }
    }

    private async Task ProcessRequestCancellationAsync(IpcFrame frame)
    {
        if (frame.Payload.Length != 0)
        {
            throw new IpcProtocolException(
                IpcProtocolErrorCode.InvalidPayloadLength,
                "An IPC cancellation frame must not contain a payload.");
        }

        if (_activeRequests.TryGetValue(frame.Header.CorrelationId, out var execution))
        {
            try
            {
                execution.CancellationSource.Cancel();
            }
            catch (ObjectDisposedException)
            {
            }

            // Keep the active-request permit held until the cancelled handler has
            // unwound. Its finally block removes the request and releases the
            // permit; admitting the next frame earlier creates a limit race.
            if (execution.Task is { } task)
            {
                try
                {
                    await task;
                }
                catch (OperationCanceledException) when (
                    execution.CancellationSource.IsCancellationRequested)
                {
                }
            }
        }
    }

    private void CancelActiveRequests(ICollection<Exception> cleanupFailures)
    {
        foreach (var execution in _activeRequests.Values)
        {
            try
            {
                execution.CancellationSource.Cancel();
            }
            catch (ObjectDisposedException)
            {
            }
            catch (Exception exception)
            {
                cleanupFailures.Add(exception);
            }
        }
    }

    private async Task<IReadOnlyCollection<Exception>> AwaitActiveRequestsAsync()
    {
        var failures = new List<Exception>();
        while (_activeRequests.Count > 0)
        {
            var tasks = _activeRequests.Values
                .Select(execution => execution.Task)
                .Where(task => task is not null)
                .Cast<Task>()
                .ToArray();
            if (tasks.Length == 0)
            {
                await Task.Yield();
                continue;
            }

            foreach (var task in tasks)
            {
                try
                {
                    await task;
                }
                catch (Exception exception)
                {
                    failures.Add(exception);
                }
            }
        }

        return failures;
    }

    private async Task SendDuplicateHandshakeRejectionAsync(
        long correlationId,
        CancellationToken cancellationToken)
    {
        await SendHandshakeRejectionAsync(
            correlationId,
            IpcErrorCode.DuplicateHandshake,
            "The IPC handshake has already completed.",
            cancellationToken);
    }

    private async Task SendHandshakeRejectionAsync(
        long correlationId,
        IpcErrorCode errorCode,
        string safeMessage,
        CancellationToken cancellationToken,
        bool isRetryable = false)
    {
        var error = new IpcError(
            errorCode,
            IpcErrorCategory.Protocol,
            safeMessage,
            correlationId,
            DateTimeOffset.UtcNow,
            IsRetryable: isRetryable,
            RequiresProcessRestart: false);
        var response = new IpcHandshakeResponse(
            Accepted: false,
            WindowsIpcProtocol.CurrentVersion,
            _options.ServerRole,
            _connection.ConnectionId,
            _options.Capabilities,
            error);
        await SendHandshakeResponseAsync(correlationId, response, cancellationToken);
    }

    private async Task SendHandshakeResponseAsync(
        long correlationId,
        IpcHandshakeResponse response,
        CancellationToken cancellationToken)
    {
        _contractValidator.Validate(response);
        if (response.Error is not null && response.Error.CorrelationId != correlationId)
            throw new IpcPayloadException("The IPC handshake error correlation ID is invalid.");

        var payload = _serializer.Serialize(
            response,
            WindowsIpcJsonContext.Default.IpcHandshakeResponse);
        await _connection.WriteFrameAsync(
            new IpcFrame(
                new IpcFrameHeader(
                    WindowsIpcProtocol.CurrentVersion,
                    IpcMessageKind.HandshakeResponse,
                    IpcFrameFlags.None,
                    correlationId,
                    payload.Length),
                payload),
            cancellationToken);
    }

    private async Task SendResponseAsync(
        IpcResponseEnvelope response,
        CancellationToken cancellationToken)
    {
        var transportResponse = CreateTransportResponse(response);
        var payload = _serializer.Serialize(
            transportResponse,
            WindowsIpcJsonContext.Default.IpcResponseEnvelope);
        try
        {
            _contractValidator.ValidateSerializedResponseEnvelope(
                transportResponse.CorrelationId,
                payload);
        }
        catch (IpcPayloadLimitExceededException)
        {
            transportResponse = PayloadLimitFailure(
                response.CorrelationId,
                IpcErrorCode.SerializedEnvelopeTooLarge,
                "The serialized IPC response envelope exceeds the permitted frame size.");
            payload = _serializer.Serialize(
                transportResponse,
                WindowsIpcJsonContext.Default.IpcResponseEnvelope);
            _contractValidator.ValidateSerializedResponseEnvelope(
                transportResponse.CorrelationId,
                payload);
        }

        await _connection.WriteFrameAsync(
            new IpcFrame(
                new IpcFrameHeader(
                    WindowsIpcProtocol.CurrentVersion,
                    IpcMessageKind.Response,
                    IpcFrameFlags.None,
                    transportResponse.CorrelationId,
                    payload.Length),
                payload),
            cancellationToken);
    }

    private IpcResponseEnvelope CreateTransportResponse(IpcResponseEnvelope response)
    {
        try
        {
            _contractValidator.Validate(response);
            return response;
        }
        catch (IpcPayloadLimitExceededException exception)
            when (exception.ErrorCode == IpcErrorCode.ResponsePayloadTooLarge)
        {
            return PayloadLimitFailure(
                response.CorrelationId,
                exception.ErrorCode,
                exception.SafeMessage);
        }
    }

    private static IpcResponseEnvelope PayloadLimitFailure(
        long correlationId,
        IpcErrorCode errorCode,
        string safeMessage) =>
        Failure(
            correlationId,
            errorCode,
            IpcErrorCategory.Validation,
            safeMessage,
            isRetryable: false);

    private async Task<Exception?> NotifyAsync(
        IpcConnectionLifecycleNotification notification,
        CancellationToken cancellationToken)
    {
        var failures = new List<Exception>();
        foreach (var observer in _observers)
        {
            try
            {
                await observer.OnConnectionLifecycleChangedAsync(notification, cancellationToken);
            }
            catch (Exception exception)
            {
                failures.Add(exception);
            }
        }

        return CombineFailures(failures);
    }

    private void ValidateRequest(
        IpcFrameHeader header,
        IpcRequestEnvelope request)
    {
        _contractValidator.Validate(request);
        if (request.CorrelationId != header.CorrelationId)
        {
            throw new IpcProtocolException(
                IpcProtocolErrorCode.InvalidEnvelope,
                "The IPC request envelope is invalid.");
        }
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

    private async Task ObserveConnectionDisposalFailureAsync()
    {
        var failure = await CaptureConnectionDisposalFailureAsync();
        if (failure is not null)
            _backgroundCleanupFailures.Enqueue(failure);
    }

    private Exception? GetFirstBackgroundFailure()
    {
        return _backgroundFailures.TryPeek(out var failure)
            ? failure
            : null;
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

    private static void ValidateFrame(IpcFrame frame)
    {
        if (frame.Header.ProtocolVersion != WindowsIpcProtocol.CurrentVersion)
        {
            throw new IpcProtocolException(
                IpcProtocolErrorCode.UnsupportedVersion,
                "The IPC frame protocol version is not supported.");
        }

        if (!Enum.IsDefined(frame.Header.MessageKind))
        {
            throw new IpcProtocolException(
                IpcProtocolErrorCode.UnknownMessageKind,
                "The IPC frame message kind is unknown.");
        }

        if (frame.Header.Flags != IpcFrameFlags.None)
        {
            throw new IpcProtocolException(
                IpcProtocolErrorCode.InvalidFlags,
                "The IPC frame flags are invalid.");
        }

        if (frame.Header.CorrelationId <= 0)
        {
            throw new IpcProtocolException(
                IpcProtocolErrorCode.InvalidCorrelationId,
                "The IPC frame correlation ID is invalid.");
        }

        if (frame.Header.PayloadLength != frame.Payload.Length ||
            frame.Payload.Length > WindowsIpcProtocol.MaximumPayloadSize)
        {
            throw new IpcProtocolException(
                IpcProtocolErrorCode.InvalidPayloadLength,
                "The IPC frame payload length is invalid.");
        }
    }

    private static IpcResponseEnvelope Failure(
        long correlationId,
        IpcErrorCode errorCode,
        IpcErrorCategory category,
        string safeMessage,
        bool isRetryable) =>
        IpcResponseEnvelope.Failure(
            correlationId,
            new IpcError(
                errorCode,
                category,
                safeMessage,
                correlationId,
                DateTimeOffset.UtcNow,
                isRetryable,
                RequiresProcessRestart: false));

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

    private static void AddFailure(
        ICollection<Exception> failures,
        Exception? failure)
    {
        if (failure is not null && !failures.Contains(failure))
            failures.Add(failure);
    }

    private static void AddFailures(
        ICollection<Exception> failures,
        IEnumerable<Exception> additionalFailures)
    {
        foreach (var failure in additionalFailures)
            AddFailure(failures, failure);
    }

    private static Exception? CombineFailures(IEnumerable<Exception> failures)
    {
        var unique = failures
            .Distinct<Exception>(ReferenceEqualityComparer.Instance)
            .ToArray();
        return unique.Length switch
        {
            0 => null,
            1 => unique[0],
            _ => new AggregateException(unique)
        };
    }

    private static Exception? CombineFailures(Exception? first, Exception? second)
    {
        if (first is null)
            return second;
        if (second is null || ReferenceEquals(first, second))
            return first;

        return new AggregateException(first, second);
    }
}
