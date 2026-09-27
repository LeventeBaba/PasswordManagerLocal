namespace PasswordManagerLocal.Common.Backend.Abstractions.Services;

public interface IInteractiveBackendHostedService
{
    Task StartAsync(CancellationToken cancellationToken = default);
    Task StopAsync(CancellationToken cancellationToken = default);
}
