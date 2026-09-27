using PasswordManagerLocal.Windows.Ipc.Contracts;

namespace PasswordManagerLocal.Windows.Agent.Background;

public interface IWindowsBackgroundSyncCoordinator
{
    Task InitializeAsync(CancellationToken cancellationToken = default);
    Task<WindowsBackgroundSyncStateDto> GetStateAsync(
        CancellationToken cancellationToken = default);
    Task<WindowsBackgroundSyncStateDto> SetEnabledAsync(
        bool isEnabled,
        CancellationToken cancellationToken = default);
    Task<bool> SuspendForDatabaseResetAsync(
        CancellationToken cancellationToken = default);
    Task RestoreAfterDatabaseResetAsync(
        bool isEnabled,
        CancellationToken cancellationToken = default);
    Task ShutdownAsync(CancellationToken cancellationToken = default);
}
