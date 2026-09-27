using PasswordManagerLocal.Windows.Frontend.AgentConnection;

namespace PasswordManagerLocal.Windows.Tests.IPC.Infrastructure;

internal sealed class FakeWindowsAgentLauncher : IWindowsAgentLauncher
{
    public bool LaunchResult { get; set; } = true;
    public int LaunchCount { get; private set; }

    public Task<bool> LaunchAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        LaunchCount++;
        return Task.FromResult(LaunchResult);
    }
}
