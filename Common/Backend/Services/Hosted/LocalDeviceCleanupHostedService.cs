using Microsoft.Extensions.DependencyInjection;
using PasswordManagerLocal.Common.Backend.Abstractions.Persistence;
using PasswordManagerLocal.Common.Backend.Abstractions.Repositories;
using PasswordManagerLocal.Common.Backend.Abstractions.Services;

namespace PasswordManagerLocal.Common.Backend.Services.Hosted;

public sealed class LocalDeviceCleanupHostedService : IBackendHostedService
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly IDeviceIdentityService _identity;

    public LocalDeviceCleanupHostedService(IServiceScopeFactory scopeFactory, IDeviceIdentityService identity)
    {
        _scopeFactory = scopeFactory;
        _identity = identity;
    }

    public async Task StartAsync(CancellationToken ct = default)
    {
        using var scope = _scopeFactory.CreateScope();
        var devices = scope.ServiceProvider.GetRequiredService<IDeviceRepository>();
        var uow = scope.ServiceProvider.GetRequiredService<IUnitOfWork>();
        var localRows = await devices.ListLocalSelfDevicesAsync(
            _identity.LocalDeviceId,
            _identity.SignPublicKey,
            _identity.FingerprintHex,
            ct);

        if (localRows.Count == 0)
            return;

        foreach (var device in localRows)
            devices.Delete(device);

        await uow.SaveChangesAsync(ct);
    }

    public Task StopAsync(CancellationToken ct = default) => Task.CompletedTask;
}
