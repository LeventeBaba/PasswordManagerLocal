using PasswordManagerLocal.Windows.Ipc.Contracts;

namespace PasswordManagerLocal.Windows.Agent.DatabaseReset;

public interface IWindowsAgentDatabaseResetCoordinator
{
    bool IsResetting { get; }
    Task<DatabaseResetResultDto> ResetAsync(CancellationToken cancellationToken = default);
}
