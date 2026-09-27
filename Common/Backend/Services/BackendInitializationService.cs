using Microsoft.Extensions.DependencyInjection;
using PasswordManagerLocal.Common.Backend.Abstractions.Repositories;
using PasswordManagerLocal.Common.Backend.Abstractions.Services;
using PasswordManagerLocal.Common.Backend.Diagnostics;
using PasswordManagerLocal.Common.Backend.Persistence;

namespace PasswordManagerLocal.Common.Backend.Services;

public sealed class BackendInitializationService : IBackendInitializationService
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly IDeviceIdentityService _deviceIdentity;

    public BackendInitializationService(
        IServiceScopeFactory scopeFactory,
        IDeviceIdentityService deviceIdentity)
    {
        _scopeFactory = scopeFactory;
        _deviceIdentity = deviceIdentity;
    }

    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        BackendDebugLog.OperationStarted("Backend database and device identity initialization", "Initialization");

        using (var scope = _scopeFactory.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            await AppDatabaseInitializer.InitializeAsync(db, cancellationToken);
        }

        await _deviceIdentity.InitializeAsync(cancellationToken);

        bool shouldEnableSync;
        using (var scope = _scopeFactory.CreateScope())
        {
            var localUserDevices = scope.ServiceProvider.GetRequiredService<ILocalUserDeviceRepository>();
            shouldEnableSync = await localUserDevices.AnySyncOnAsync(cancellationToken);
        }

        if (_deviceIdentity.IsSyncOn != shouldEnableSync)
            await _deviceIdentity.SetSyncOnAsync(shouldEnableSync, cancellationToken);

        BackendDebugLog.Info(
            $"Backend database and device identity initialization completed successfully. " +
            $"DeviceId={_deviceIdentity.LocalDeviceId:N}, DeviceType={_deviceIdentity.DeviceType}, SyncEnabled={_deviceIdentity.IsSyncOn}.",
            "Initialization");
    }
}
