namespace PasswordManagerLocal.Windows.Agent.Endpoint;

public interface IWindowsAgentEndpointHost : IAsyncDisposable
{
    WindowsAgentEndpointHostSnapshot Snapshot { get; }
    Task Completion { get; }
    event EventHandler? StateChanged;

    Task StartAsync(CancellationToken cancellationToken = default);
    Task StopAsync(CancellationToken cancellationToken = default);
}
