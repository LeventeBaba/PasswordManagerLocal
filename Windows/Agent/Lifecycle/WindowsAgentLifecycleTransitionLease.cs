namespace PasswordManagerLocal.Windows.Agent.Lifecycle;

public sealed class WindowsAgentLifecycleTransitionLease : IAsyncDisposable
{
    private readonly WindowsAgentLifecycleTransitionCoordinator _owner;
    private int _disposed;

    internal WindowsAgentLifecycleTransitionLease(
        WindowsAgentLifecycleTransitionCoordinator owner,
        WindowsAgentLifecycleTransitionState state)
    {
        _owner = owner;
        State = state;
    }

    public WindowsAgentLifecycleTransitionState State { get; }

    public ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 0)
            _owner.Release(State);

        return ValueTask.CompletedTask;
    }
}
