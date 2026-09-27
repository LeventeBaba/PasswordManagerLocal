using PasswordManagerLocal.Windows.Frontend.AgentConnection;
using PasswordManagerLocal.Windows.Ipc.Contracts;

namespace PasswordManagerLocal.Windows.Tests.IPC.Infrastructure;

internal sealed class FakeWindowsAgentControlConnector : IWindowsAgentControlConnector
{
    private readonly Queue<WindowsAgentControlConnectionAttempt> _results = new();
    private readonly List<WindowsUiIpcIdentity> _identities = new();

    public int AttemptCount { get; private set; }
    public IReadOnlyList<WindowsUiIpcIdentity> Identities => _identities;

    public void Enqueue(IWindowsAgentRegisteredConnection? connection) =>
        _results.Enqueue(new WindowsAgentControlConnectionAttempt(
            connection,
            connection?.AgentProcessId));

    public void EnqueueObservedUnavailable(int agentProcessId) =>
        _results.Enqueue(new WindowsAgentControlConnectionAttempt(null, agentProcessId));

    public Task<WindowsAgentControlConnectionAttempt> TryConnectAndRegisterAsync(
        string pipeName,
        WindowsUiIpcIdentity identity,
        TimeSpan connectTimeout,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        AttemptCount++;
        _identities.Add(identity);
        return Task.FromResult(_results.Count == 0
            ? new WindowsAgentControlConnectionAttempt(null, null)
            : _results.Dequeue());
    }
}
