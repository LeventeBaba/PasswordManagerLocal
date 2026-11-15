namespace PasswordManagerLocal.Common.Backend.Abstractions.Services;

/// <summary>Passive authenticated ingress has a separate lifetime from ordinary synchronization.</summary>
public interface IPassiveSyncRuntime : ISyncRuntimeHealth
{
    Task StartAsync(CancellationToken ct = default);
    Task StopAsync(CancellationToken ct = default);
}
