namespace PasswordManagerLocal.Windows.Agent.Lifecycle;

public sealed class WindowsAgentLifecycleTransitionCoordinator : IDisposable
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly object _stateGate = new();
    private WindowsAgentLifecycleTransitionState _state;
    private bool _disposed;

    public WindowsAgentLifecycleTransitionState State
    {
        get
        {
            lock (_stateGate)
                return _state;
        }
    }

    public async Task<WindowsAgentLifecycleTransitionLease> EnterAsync(
        WindowsAgentLifecycleTransitionState state,
        CancellationToken cancellationToken = default)
    {
        if (state == WindowsAgentLifecycleTransitionState.Idle || !Enum.IsDefined(state))
            throw new ArgumentOutOfRangeException(nameof(state));

        await _gate.WaitAsync(cancellationToken);
        lock (_stateGate)
        {
            if (_disposed)
            {
                _gate.Release();
                throw new ObjectDisposedException(nameof(WindowsAgentLifecycleTransitionCoordinator));
            }

            _state = state;
        }

        return new WindowsAgentLifecycleTransitionLease(this, state);
    }

    internal void Release(WindowsAgentLifecycleTransitionState state)
    {
        lock (_stateGate)
        {
            if (_state != state)
                throw new InvalidOperationException("The Windows agent lifecycle transition owner is inconsistent.");

            _state = WindowsAgentLifecycleTransitionState.Idle;
        }

        _gate.Release();
    }

    public void Dispose()
    {
        lock (_stateGate)
        {
            if (_disposed)
                return;
            if (_state != WindowsAgentLifecycleTransitionState.Idle)
                throw new InvalidOperationException("An active lifecycle transition cannot be disposed.");

            _disposed = true;
        }

        _gate.Dispose();
    }
}
