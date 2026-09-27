using PasswordManagerLocal.Windows.Agent.DatabaseReset;
using PasswordManagerLocal.Windows.Ipc.Contracts;

namespace PasswordManagerLocal.Windows.Tests.IPC.Infrastructure;

internal sealed class FakeWindowsAgentDatabaseResetCoordinator : IWindowsAgentDatabaseResetCoordinator
{
    public bool IsResetting { get; set; }
    public DatabaseResetResultDto Result { get; set; } = new(true, false, null);
    public int ResetCount { get; private set; }

    public Task<DatabaseResetResultDto> ResetAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ResetCount++;
        return Task.FromResult(Result);
    }
}
