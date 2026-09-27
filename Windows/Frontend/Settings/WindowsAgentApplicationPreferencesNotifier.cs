using PasswordManagerLocal.Common.Frontend.Services;
using PasswordManagerLocal.Windows.Frontend.AgentConnection;

namespace PasswordManagerLocal.Windows.Frontend.Settings;

public sealed class WindowsAgentApplicationPreferencesNotifier : IApplicationPreferencesChangeNotifier
{
    private readonly IWindowsAgentControlConnection _agentConnection;

    public WindowsAgentApplicationPreferencesNotifier(IWindowsAgentControlConnection agentConnection) =>
        _agentConnection = agentConnection ?? throw new ArgumentNullException(nameof(agentConnection));

    public async Task NotifyLanguagePersistedAsync(CancellationToken cancellationToken = default)
    {
        var reloaded = await _agentConnection.ReloadApplicationPreferencesAsync(cancellationToken)
            .ConfigureAwait(false);
        if (!reloaded)
            throw new InvalidOperationException("The Windows Agent rejected the application-preference reload request.");
    }
}
