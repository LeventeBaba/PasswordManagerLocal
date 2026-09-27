namespace PasswordManagerLocal.Common.Backend.Abstractions.Services;

public interface IBackendHostedService
{
    Task StartAsync(CancellationToken cancellationToken = default);

    Task StopAsync(CancellationToken cancellationToken = default);
}
