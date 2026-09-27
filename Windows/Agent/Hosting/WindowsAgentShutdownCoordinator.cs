namespace PasswordManagerLocal.Windows.Agent.Hosting;

public sealed class WindowsAgentShutdownCoordinator
{
    public event EventHandler<WindowsAgentShutdownRequestedEventArgs>? ShutdownRequested;

    public void RequestShutdown(WindowsAgentShutdownReason reason)
    {
        if (!Enum.IsDefined(reason))
            throw new ArgumentOutOfRangeException(nameof(reason));

        ShutdownRequested?.Invoke(this, new WindowsAgentShutdownRequestedEventArgs(reason));
    }
}
