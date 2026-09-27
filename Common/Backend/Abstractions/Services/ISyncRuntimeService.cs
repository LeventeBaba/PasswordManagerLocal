using PasswordManagerLocal.Common.Backend.Models;

namespace PasswordManagerLocal.Common.Backend.Abstractions.Services;

public interface ISyncRuntimeService
{
    SyncRuntimeSnapshot Snapshot { get; }

    event EventHandler<SyncRuntimeStateChangedEventArgs>? StateChanged;

    Task RefreshSyncEnabledAsync(CancellationToken ct = default);
    Task BeginEnrollmentOnlyAsync(CancellationToken ct = default);
    Task EndEnrollmentOnlyAsync(CancellationToken ct = default);
    Task StartAsync(CancellationToken ct = default);
    Task StopAsync(CancellationToken ct = default);
}
