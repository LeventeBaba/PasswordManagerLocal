using PasswordManagerLocal.Windows.Ipc.Contracts;

namespace PasswordManagerLocal.Windows.Agent.Hosting;

public sealed class WindowsAgentStateStore : IWindowsAgentStateSource
{
    private readonly object _gate = new();
    private AgentState _state = AgentState.Starting;
    private DateTimeOffset? _startedAtUtc;
    private IpcFailureDto? _lastFailure;

    public AgentState State
    {
        get { lock (_gate) return _state; }
    }

    public DateTimeOffset? StartedAtUtc
    {
        get { lock (_gate) return _startedAtUtc; }
    }

    public IpcFailureDto? LastFailure
    {
        get { lock (_gate) return _lastFailure; }
    }

    public event EventHandler<WindowsAgentStateChangedEventArgs>? StateChanged;

    public void MarkRunning(DateTimeOffset startedAtUtc)
    {
        if (startedAtUtc == default || startedAtUtc.Offset != TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(startedAtUtc));
        if (!TryTransition(AgentState.Starting, AgentState.Running, startedAtUtc, null))
            throw new InvalidOperationException("The Windows agent can enter Running only from Starting.");
    }

    public void MarkFailed(string safeMessage)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(safeMessage);
        AgentState previous;
        lock (_gate)
        {
            if (_state is AgentState.Stopping or AgentState.Stopped)
                return;

            previous = _state;
            _state = AgentState.Failed;
            _lastFailure = new IpcFailureDto(
                IpcFailureKind.AgentShell,
                safeMessage,
                DateTimeOffset.UtcNow,
                IsRetryable: false,
                RequiresProcessRestart: false);
        }

        Publish(previous, AgentState.Failed);
    }


    public void MarkShutdownFailed(
        string safeMessage,
        bool requiresProcessRestart)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(safeMessage);
        AgentState previous;
        lock (_gate)
        {
            if (_state == AgentState.Stopped)
                return;

            previous = _state;
            _state = AgentState.Failed;
            _startedAtUtc = null;
            _lastFailure = new IpcFailureDto(
                IpcFailureKind.AgentShell,
                safeMessage,
                DateTimeOffset.UtcNow,
                IsRetryable: true,
                RequiresProcessRestart: requiresProcessRestart);
        }

        Publish(previous, AgentState.Failed);
    }

    public void MarkStopping()
    {
        AgentState previous;
        lock (_gate)
        {
            if (_state is AgentState.Stopping or AgentState.Stopped)
                return;

            previous = _state;
            _state = AgentState.Stopping;
        }

        Publish(previous, AgentState.Stopping);
    }

    public void MarkStopped()
    {
        AgentState previous;
        lock (_gate)
        {
            if (_state == AgentState.Stopped)
                return;
            if (_state != AgentState.Stopping)
                throw new InvalidOperationException("The Windows agent can enter Stopped only from Stopping.");

            previous = _state;
            _state = AgentState.Stopped;
            _startedAtUtc = null;
            _lastFailure = null;
        }

        Publish(previous, AgentState.Stopped);
    }

    private bool TryTransition(
        AgentState expected,
        AgentState next,
        DateTimeOffset? startedAtUtc,
        IpcFailureDto? failure)
    {
        lock (_gate)
        {
            if (_state != expected)
                return false;

            _state = next;
            _startedAtUtc = startedAtUtc;
            _lastFailure = failure;
        }

        Publish(expected, next);
        return true;
    }

    private void Publish(AgentState previous, AgentState current)
    {
        var handlers = StateChanged;
        if (handlers is null)
            return;

        var args = new WindowsAgentStateChangedEventArgs(previous, current);
        foreach (EventHandler<WindowsAgentStateChangedEventArgs> handler in handlers.GetInvocationList())
        {
            try { handler(this, args); } catch { }
        }
    }
}
