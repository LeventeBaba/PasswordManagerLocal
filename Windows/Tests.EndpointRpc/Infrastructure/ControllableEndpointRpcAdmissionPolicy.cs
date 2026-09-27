using PasswordManagerLocal.Windows.EndpointRpc.Authorization;

namespace PasswordManagerLocal.Windows.Tests.EndpointRpc.Infrastructure;

internal sealed class ControllableEndpointRpcAdmissionPolicy : IEndpointRpcAdmissionPolicy
{
    private readonly object _gate = new();
    private TaskCompletionSource? _drained;
    private int _activeRequests;

    public bool CanAcceptConnection { get; set; } = true;
    public int ActiveRequests
    {
        get { lock (_gate) return _activeRequests; }
    }

    public bool TryEnterRequest(out IDisposable? lease)
    {
        lock (_gate)
        {
            if (!CanAcceptConnection)
            {
                lease = null;
                return false;
            }

            _activeRequests++;
            lease = new EndpointRpcAdmissionTestLease(Release);
            return true;
        }
    }

    public Task WaitForDrainAsync(CancellationToken cancellationToken = default)
    {
        Task task;
        lock (_gate)
        {
            if (_activeRequests == 0)
                return Task.CompletedTask;
            _drained ??= new TaskCompletionSource(
                TaskCreationOptions.RunContinuationsAsynchronously);
            task = _drained.Task;
        }

        return cancellationToken.CanBeCanceled
            ? task.WaitAsync(cancellationToken)
            : task;
    }

    private void Release()
    {
        TaskCompletionSource? drained = null;
        lock (_gate)
        {
            _activeRequests--;
            if (_activeRequests == 0)
            {
                drained = _drained;
                _drained = null;
            }
        }
        drained?.TrySetResult();
    }
}
