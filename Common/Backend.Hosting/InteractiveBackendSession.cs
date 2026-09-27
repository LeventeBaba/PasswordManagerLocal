using PasswordManagerLocal.Common.Contracts.Endpoints;
using PasswordManagerLocal.Common.Backend.Abstractions.Services;

namespace PasswordManagerLocal.Common.Backend.Hosting;

internal sealed class InteractiveBackendSession : IInteractiveBackendSession
{
    private readonly Func<InteractiveBackendSession, ValueTask> _release;
    private readonly IInteractiveSessionStateService _sessionState;
    private readonly object _operationLock = new();
    private readonly TaskCompletionSource _disposeCompletion = new(
        TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly SessionBoundEndpoints _sessionEndpoints;
    private IEndpoints? _endpoints;
    private TaskCompletionSource? _operationsDrained;
    private int _activeOperations;
    private int _releaseStarted;
    private bool _closing;

    public InteractiveBackendSession(
        IEndpoints endpoints,
        IInteractiveSessionStateService sessionState,
        Func<InteractiveBackendSession, ValueTask> release)
    {
        _endpoints = endpoints ?? throw new ArgumentNullException(nameof(endpoints));
        _sessionState = sessionState ?? throw new ArgumentNullException(nameof(sessionState));
        _release = release ?? throw new ArgumentNullException(nameof(release));
        _sessionEndpoints = new SessionBoundEndpoints(this);
    }

    public bool AcceptsNewOperations
    {
        get { lock (_operationLock) return !_closing && _endpoints is not null; }
    }

    public bool IsClosing
    {
        get { lock (_operationLock) return _closing; }
    }

    public int ActiveOperationCount
    {
        get { lock (_operationLock) return _activeOperations; }
    }

    public IEndpoints Endpoints
    {
        get
        {
            lock (_operationLock)
            {
                if (_closing)
                    throw new ObjectDisposedException(nameof(InteractiveBackendSession));

                return _sessionEndpoints;
            }
        }
    }

    internal async Task ExecuteAsync(Func<IEndpoints, Task> operation)
    {
        ArgumentNullException.ThrowIfNull(operation);
        var (endpoints, stateLease) = BeginOperation();
        try
        {
            await operation(endpoints);
        }
        finally
        {
            try
            {
                stateLease.Dispose();
            }
            finally
            {
                EndOperation();
            }
        }
    }

    internal async Task<T> ExecuteAsync<T>(Func<IEndpoints, Task<T>> operation)
    {
        ArgumentNullException.ThrowIfNull(operation);
        var (endpoints, stateLease) = BeginOperation();
        try
        {
            return await operation(endpoints);
        }
        finally
        {
            try
            {
                stateLease.Dispose();
            }
            finally
            {
                EndOperation();
            }
        }
    }

    internal Task BeginCloseAsync(Action? closingStarted = null)
    {
        Task operationDrain;

        lock (_operationLock)
        {
            _closing = true;

            if (_activeOperations == 0)
            {
                _endpoints = null;
                operationDrain = Task.CompletedTask;
            }
            else
            {
                _operationsDrained ??= new TaskCompletionSource(
                    TaskCreationOptions.RunContinuationsAsynchronously);
                operationDrain = _operationsDrained.Task;
            }
        }

        Task closingStartedTask;
        try
        {
            closingStarted?.Invoke();
            closingStartedTask = Task.CompletedTask;
        }
        catch (Exception exception)
        {
            closingStartedTask = Task.FromException(exception);
        }

        Task stateDrain;
        try
        {
            stateDrain = _sessionState.DeactivateAsync(CancellationToken.None);
        }
        catch (Exception exception)
        {
            stateDrain = Task.FromException(exception);
        }

        return Task.WhenAll(operationDrain, closingStartedTask, stateDrain);
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.CompareExchange(ref _releaseStarted, 1, 0) != 0)
        {
            await _disposeCompletion.Task;
            return;
        }

        try
        {
            await _release(this);
            _disposeCompletion.TrySetResult();
            GC.SuppressFinalize(this);
        }
        catch (Exception exception)
        {
            _disposeCompletion.TrySetException(exception);
            throw;
        }
    }

    private (IEndpoints Endpoints, IDisposable StateLease) BeginOperation()
    {
        lock (_operationLock)
        {
            if (_closing || _endpoints is null)
                throw new ObjectDisposedException(nameof(InteractiveBackendSession));

            var stateLease = _sessionState.EnterOperation();
            _activeOperations++;
            return (_endpoints, stateLease);
        }
    }

    private void EndOperation()
    {
        TaskCompletionSource? operationsDrained = null;
        lock (_operationLock)
        {
            _activeOperations--;
            if (_closing && _activeOperations == 0)
            {
                _endpoints = null;
                operationsDrained = _operationsDrained;
                _operationsDrained = null;
            }
        }

        operationsDrained?.TrySetResult();
    }
}
