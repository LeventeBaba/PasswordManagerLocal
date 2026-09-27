using PasswordManagerLocal.Windows.Ipc.Contracts;

namespace PasswordManagerLocal.Windows.Ipc.Client;

internal sealed class PendingIpcRequest
{
    private readonly TaskCompletionSource<IpcResponseEnvelope> _completion = new(
        TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly Action<PendingIpcRequest> _releaseOwnership;
    private readonly CancellationToken _callerCancellationToken;
    private readonly CancellationTokenSource _queuedCancellationSource = new();
    private readonly CancellationToken _queuedCancellationToken;
    private readonly object _stateGate = new();
    private CancellationTokenRegistration _cancellationRegistration;
    private IpcRequestSubmissionState _submissionState = IpcRequestSubmissionState.Created;
    private IpcRequestTransmissionState _transmissionState = IpcRequestTransmissionState.DefinitelyNotSent;
    private bool _hasCancellationRegistration;
    private bool _deferredCallerCancellation;
    private IpcResponseEnvelope? _deferredResponse;
    private bool _remoteCancellationStarted;
    private int _ownershipReleased;

    public PendingIpcRequest(
        long correlationId,
        CancellationToken callerCancellationToken,
        Action<PendingIpcRequest> releaseOwnership)
    {
        if (correlationId <= 0)
            throw new ArgumentOutOfRangeException(nameof(correlationId));

        CorrelationId = correlationId;
        _callerCancellationToken = callerCancellationToken;
        _queuedCancellationToken = _queuedCancellationSource.Token;
        _releaseOwnership = releaseOwnership
            ?? throw new ArgumentNullException(nameof(releaseOwnership));
    }

    public long CorrelationId { get; }
    public Task<IpcResponseEnvelope> Task => _completion.Task;
    public CancellationToken QueuedCancellationToken => _queuedCancellationToken;

    public IpcRequestTransmissionState TransmissionState
    {
        get
        {
            lock (_stateGate)
                return _transmissionState;
        }
    }

    public IpcRequestSubmissionState SubmissionState
    {
        get
        {
            lock (_stateGate)
                return _submissionState;
        }
    }

    public void RegisterCallerCancellation(Action callback)
    {
        ArgumentNullException.ThrowIfNull(callback);
        if (!_callerCancellationToken.CanBeCanceled)
            return;

        CancellationTokenRegistration registration;
        try
        {
            registration = _callerCancellationToken.Register(callback);
        }
        catch (ObjectDisposedException exception)
        {
            TrySetException(exception);
            return;
        }

        var disposeRegistration = false;
        lock (_stateGate)
        {
            if (_submissionState == IpcRequestSubmissionState.Completed)
            {
                disposeRegistration = true;
            }
            else
            {
                _cancellationRegistration = registration;
                _hasCancellationRegistration = true;
            }
        }

        if (disposeRegistration)
            registration.Dispose();
    }

    public void DisposeCancellationRegistration()
    {
        CancellationTokenRegistration registration;
        lock (_stateGate)
        {
            if (!_hasCancellationRegistration)
                return;

            registration = _cancellationRegistration;
            _cancellationRegistration = default;
            _hasCancellationRegistration = false;
        }

        registration.Dispose();
    }

    public bool TryRegisterPending(Func<bool> tryRegister)
    {
        ArgumentNullException.ThrowIfNull(tryRegister);
        lock (_stateGate)
        {
            if (_submissionState == IpcRequestSubmissionState.Completed)
                return false;
            if (_submissionState != IpcRequestSubmissionState.Created)
                throw new InvalidOperationException("The IPC request cannot be registered in its current state.");
            if (!tryRegister())
                throw new InvalidOperationException("The IPC correlation ID is already pending.");

            return true;
        }
    }

    public bool TryQueue()
    {
        lock (_stateGate)
        {
            if (_submissionState != IpcRequestSubmissionState.Created)
                return false;

            _submissionState = IpcRequestSubmissionState.Queued;
            return true;
        }
    }

    public bool TryBeginSending()
    {
        lock (_stateGate)
        {
            if (_submissionState != IpcRequestSubmissionState.Queued)
                return false;

            _submissionState = IpcRequestSubmissionState.Sending;
            _transmissionState = IpcRequestTransmissionState.TransmissionUnknown;
            return true;
        }
    }

    public bool CommitSent()
    {
        var completeAsCancelled = false;
        IpcResponseEnvelope? deferredResponse = null;
        var sendRemoteCancellation = false;
        lock (_stateGate)
        {
            if (_submissionState == IpcRequestSubmissionState.Completed)
                return false;
            if (_submissionState != IpcRequestSubmissionState.Sending)
                throw new InvalidOperationException("The IPC request is not being sent.");

            _submissionState = IpcRequestSubmissionState.Sent;
            _transmissionState = IpcRequestTransmissionState.Sent;
            if (_deferredCallerCancellation)
            {
                _submissionState = IpcRequestSubmissionState.Completed;
                completeAsCancelled = true;
                sendRemoteCancellation = TryStartRemoteCancellationLocked();
            }
            else if (_deferredResponse is not null)
            {
                _submissionState = IpcRequestSubmissionState.Completed;
                deferredResponse = _deferredResponse;
                _deferredResponse = null;
            }
        }

        if (completeAsCancelled)
            Complete(() => _completion.TrySetCanceled(_callerCancellationToken));
        else if (deferredResponse is not null)
            Complete(() => _completion.TrySetResult(deferredResponse));

        return sendRemoteCancellation;
    }

    public bool RequestCallerCancellation()
    {
        var completeAsCancelled = false;
        var cancelQueuedWrite = false;
        var sendRemoteCancellation = false;
        lock (_stateGate)
        {
            if (_submissionState == IpcRequestSubmissionState.Completed)
                return false;

            switch (_submissionState)
            {
                case IpcRequestSubmissionState.Created:
                case IpcRequestSubmissionState.Queued:
                    _submissionState = IpcRequestSubmissionState.Completed;
                    completeAsCancelled = true;
                    cancelQueuedWrite = true;
                    break;
                case IpcRequestSubmissionState.Sending:
                    if (_deferredResponse is null)
                        _deferredCallerCancellation = true;
                    break;
                case IpcRequestSubmissionState.Sent:
                    _submissionState = IpcRequestSubmissionState.Completed;
                    completeAsCancelled = true;
                    sendRemoteCancellation = TryStartRemoteCancellationLocked();
                    break;
                default:
                    throw new InvalidOperationException("The IPC request submission state is invalid.");
            }
        }

        if (cancelQueuedWrite)
            _queuedCancellationSource.Cancel();
        if (completeAsCancelled)
            Complete(() => _completion.TrySetCanceled(_callerCancellationToken));

        return sendRemoteCancellation;
    }

    public bool TrySetResult(IpcResponseEnvelope response)
    {
        ArgumentNullException.ThrowIfNull(response);
        var complete = false;
        lock (_stateGate)
        {
            if (_submissionState is IpcRequestSubmissionState.Created or
                IpcRequestSubmissionState.Queued or
                IpcRequestSubmissionState.Completed)
            {
                return false;
            }

            if (_submissionState == IpcRequestSubmissionState.Sending)
            {
                if (_deferredCallerCancellation || _deferredResponse is not null)
                    return false;

                _deferredResponse = response;
                return true;
            }

            _submissionState = IpcRequestSubmissionState.Completed;
            complete = true;
        }

        if (complete)
            Complete(() => _completion.TrySetResult(response));
        return true;
    }

    public bool TrySetException(Exception exception)
    {
        ArgumentNullException.ThrowIfNull(exception);
        lock (_stateGate)
        {
            if (_submissionState == IpcRequestSubmissionState.Completed)
                return false;

            _submissionState = IpcRequestSubmissionState.Completed;
        }

        Complete(() => _completion.TrySetException(exception));
        return true;
    }

    private bool TryStartRemoteCancellationLocked()
    {
        if (_remoteCancellationStarted)
            return false;

        _remoteCancellationStarted = true;
        return true;
    }

    private void Complete(Action complete)
    {
        if (Interlocked.Exchange(ref _ownershipReleased, 1) != 0)
            return;

        _releaseOwnership(this);
        _queuedCancellationSource.Dispose();
        complete();
    }
}
