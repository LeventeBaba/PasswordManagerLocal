namespace PasswordManagerLocal.Windows.Agent.Hosting;

public interface IWindowsAgentHost : IAsyncDisposable
{
    bool RetainsProcessOwnershipUntilTermination { get; }

    Task StartAsync(CancellationToken cancellationToken = default);
    Task ShutdownAsync(CancellationToken cancellationToken = default);
    Task<WindowsAgentShutdownResult> RequestShutdownAsync(
        WindowsAgentShutdownReason reason,
        CancellationToken cancellationToken = default);
}
