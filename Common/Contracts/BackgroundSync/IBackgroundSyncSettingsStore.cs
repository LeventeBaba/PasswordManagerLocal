
namespace PasswordManagerLocal.Common.Contracts.BackgroundSync;

public interface IBackgroundSyncSettingsStore
{
    Task<BackgroundSyncSettings> ReadAsync(CancellationToken cancellationToken = default);
    Task WriteAsync(
        BackgroundSyncSettings settings,
        CancellationToken cancellationToken = default);
}
