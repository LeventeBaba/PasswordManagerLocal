using Microsoft.Extensions.DependencyInjection;
using Microsoft.Data.Sqlite;
using PasswordManagerLocal.Common.Backend.Abstractions.Repositories;
using PasswordManagerLocal.Common.Backend.Abstractions.Services;
using PasswordManagerLocal.Common.Backend.Diagnostics;
using PasswordManagerLocal.Common.Backend.Persistence;
using PasswordManagerLocal.Common.Backend.Configuration;

namespace PasswordManagerLocal.Common.Backend.Services;

internal sealed class BackendInitializationService : IBackendInitializationService
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly IDeviceIdentityService _deviceIdentity;
    private readonly BackendStoragePaths _storagePaths;
    private readonly SqliteConnectionFactory _connections;

    public BackendInitializationService(
        IServiceScopeFactory scopeFactory,
        IDeviceIdentityService deviceIdentity,
        BackendStoragePaths storagePaths,
        SqliteConnectionFactory connections)
    {
        _scopeFactory = scopeFactory;
        _deviceIdentity = deviceIdentity;
        _storagePaths = storagePaths;
        _connections = connections;
    }

    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            await InitializeCoreAsync(cancellationToken);
        }
        catch (SqliteException exception) when (exception.SqliteErrorCode is 11 or 26)
        {
            // CORRUPT and NOTADB do not reliably distinguish a mismatched encryption
            // key from damaged encrypted data. Busy, permission and I/O errors remain
            // ordinary startup/storage failures and do not authorize deletion.
            throw new DatabaseUnreadableException(exception);
        }
    }

    private async Task InitializeCoreAsync(CancellationToken cancellationToken)
    {
        BackendDebugLog.OperationStarted("Backend database and device identity initialization", "Initialization");

        if (await DatabaseEncryptionMigrator.MigrateIfRequiredAsync(
                _storagePaths,
                _connections.GetDatabasePassword(),
                cancellationToken))
        {
            BackendDebugLog.Info(
                "The existing SQLCipher database was migrated to the SQLite3MC ChaCha20 cipher.",
                "Initialization");
        }

        await using (var connection = _connections.CreateConnection())
        {
            await AppDatabaseInitializer.InitializeAsync(connection, cancellationToken);
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
