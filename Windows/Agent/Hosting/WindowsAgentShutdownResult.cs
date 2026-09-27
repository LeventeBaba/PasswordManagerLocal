namespace PasswordManagerLocal.Windows.Agent.Hosting;

public sealed record WindowsAgentShutdownResult(
    WindowsAgentShutdownResultKind Kind,
    string? SafeMessage = null,
    Exception? Failure = null)
{
    public bool IsCompleted => Kind == WindowsAgentShutdownResultKind.Completed;
}
