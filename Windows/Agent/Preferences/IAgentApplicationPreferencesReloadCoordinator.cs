namespace PasswordManagerLocal.Windows.Agent.Preferences;

public interface IAgentApplicationPreferencesReloadCoordinator : IAsyncDisposable
{
    Task<bool> ReloadAsync(CancellationToken cancellationToken = default);
}
