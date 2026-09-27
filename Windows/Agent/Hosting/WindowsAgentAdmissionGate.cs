using PasswordManagerLocal.Windows.Ipc.Contracts;
using PasswordManagerLocal.Windows.Ipc.Lifecycle;

namespace PasswordManagerLocal.Windows.Agent.Hosting;

public sealed class WindowsAgentAdmissionGate : IWindowsAgentAdmissionGate
{
    private readonly object _gate = new();
    private TaskCompletionSource? _drained;
    private AgentAdmissionState _state = AgentAdmissionState.Closed;
    private int _activeAdmissions;
    private bool _opened;
    private bool _permanentlyClosed;

    public AgentAdmissionState State
    {
        get { lock (_gate) return _state; }
    }

    public bool IsOpen
    {
        get { lock (_gate) return _state == AgentAdmissionState.Open; }
    }

    public void Open()
    {
        lock (_gate)
        {
            if (_permanentlyClosed || _opened || _activeAdmissions != 0)
                throw new InvalidOperationException("The Windows agent admission gate cannot be reopened.");

            _opened = true;
            _state = AgentAdmissionState.Open;
        }
    }

    public void ClosePermanently()
    {
        TaskCompletionSource? drained = null;
        lock (_gate)
        {
            _permanentlyClosed = true;
            if (_activeAdmissions == 0)
            {
                _state = AgentAdmissionState.Closed;
                drained = _drained;
                _drained = null;
            }
            else
            {
                _state = AgentAdmissionState.Closing;
                _drained ??= new TaskCompletionSource(
                    TaskCreationOptions.RunContinuationsAsynchronously);
            }
        }

        drained?.TrySetResult();
    }

    public bool TryEnter(out IDisposable? lease)
    {
        lock (_gate)
        {
            if (_state != AgentAdmissionState.Open)
            {
                lease = null;
                return false;
            }

            _activeAdmissions++;
            lease = new WindowsAgentAdmissionLease(Release);
            return true;
        }
    }

    public Task WaitForDrainAsync(CancellationToken cancellationToken = default)
    {
        Task task;
        lock (_gate)
        {
            if (_activeAdmissions == 0)
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
            if (_activeAdmissions <= 0)
                throw new InvalidOperationException("The Windows agent admission count is invalid.");

            _activeAdmissions--;
            if (_activeAdmissions == 0)
            {
                if (_state == AgentAdmissionState.Closing)
                    _state = AgentAdmissionState.Closed;
                drained = _drained;
                _drained = null;
            }
        }

        drained?.TrySetResult();
    }
}