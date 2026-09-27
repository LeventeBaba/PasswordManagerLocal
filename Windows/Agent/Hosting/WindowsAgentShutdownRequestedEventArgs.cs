namespace PasswordManagerLocal.Windows.Agent.Hosting;

public sealed class WindowsAgentShutdownRequestedEventArgs : EventArgs
{
    public WindowsAgentShutdownRequestedEventArgs(WindowsAgentShutdownReason reason) =>
        Reason = reason;

    public WindowsAgentShutdownReason Reason { get; }
}
