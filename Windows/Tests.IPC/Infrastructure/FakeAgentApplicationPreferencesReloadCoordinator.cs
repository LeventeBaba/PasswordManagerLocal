using PasswordManagerLocal.Windows.Agent.Preferences;

namespace PasswordManagerLocal.Windows.Tests.IPC.Infrastructure;

internal sealed class FakeAgentApplicationPreferencesReloadCoordinator : IAgentApplicationPreferencesReloadCoordinator
{
    public int ReloadCount { get; private set; }
    public int DisposeCount { get; private set; }
    public bool ReloadResult { get; set; } = true;
    public ICollection<string>? OperationLog { get; set; }

    public Task<bool> ReloadAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ReloadCount++;
        OperationLog?.Add("localization-reload");
        return Task.FromResult(ReloadResult);
    }

    public ValueTask DisposeAsync()
    {
        DisposeCount++;
        OperationLog?.Add("localization-stop");
        return ValueTask.CompletedTask;
    }
}
