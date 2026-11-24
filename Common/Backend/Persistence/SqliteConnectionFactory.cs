using Microsoft.Data.Sqlite;
using System.Data;
using PasswordManagerLocal.Common.Backend.Abstractions.Security;
using PasswordManagerLocal.Common.Backend.Configuration;
using PasswordManagerLocal.Common.Backend.Security;
using PasswordManagerLocal.Common.Backend.Constants;

namespace PasswordManagerLocal.Common.Backend.Persistence;

internal sealed class SqliteConnectionFactory
{
    private readonly Lazy<string> _connectionString;
    private readonly SqliteWalPressure _walPressure;
    private readonly long _maximumDatabaseBytes;
    private readonly SqlitePhysicalLimits _physicalLimits;

    public SqliteConnectionFactory(BackendStoragePaths paths, IKeyProtector keyProtector)
    {
        _walPressure = new();
        _maximumDatabaseBytes = StorageCapacityConstants.MainDatabaseBytes;
        _physicalLimits = SqlitePhysicalLimits.Default;
        // Share the protected key lookup across schema bootstrap and runtime callers
        // for this host generation. Startup still migrates before opening the vault.
        _connectionString = new(() =>
        {
            var password = DbConfigManager.GetOrCreateDatabasePassword(paths, keyProtector);
            return DatabaseEncryptionMigrator.CreateRuntimeConnectionString(paths.DatabasePath, password);
        });
    }

    internal SqliteConnectionFactory(string connectionString,
        long walPressureBytes = StorageCapacityConstants.WalPressureBytes, TimeProvider? timeProvider = null,
        long maximumDatabaseBytes = StorageCapacityConstants.MainDatabaseBytes,
        SqlitePhysicalLimits? physicalLimits = null)
    {
        _connectionString = new(() => connectionString);
        _walPressure = new(walPressureBytes, timeProvider);
        if (maximumDatabaseBytes <= 0 || maximumDatabaseBytes > StorageCapacityConstants.MainDatabaseBytes)
            throw new ArgumentOutOfRangeException(nameof(maximumDatabaseBytes));
        _physicalLimits = physicalLimits ?? SqlitePhysicalLimits.Default;
        _physicalLimits.Validate();
        // A committed WAL must never describe more main-image pages than a later checkpoint
        // is allowed to write, including when a reduced physical policy is used by tests.
        _maximumDatabaseBytes = Math.Min(maximumDatabaseBytes, _physicalLimits.MainBytes);
    }

    private string ConnectionString => _connectionString.Value;

    public string GetDatabasePassword() => new SqliteConnectionStringBuilder(ConnectionString).Password;
    internal long WalPressureThresholdBytes => _walPressure.PressureBytes;

    internal void TryRelieveWalPressure(SqliteConnection connection) => _walPressure.TryCheckpoint(connection);

    public SqliteConnection CreateConnection(bool enforceForeignKeys = false)
    {
        SqliteProviderInitializer.EnsureInitialized();
        var options = new SqliteConnectionStringBuilder(ConnectionString);
        options.Vfs = SqliteBoundedVfs.GetName(_physicalLimits);
        if (enforceForeignKeys)
            options.ForeignKeys = true;
        var connection = new SqliteConnection(options.ToString());
        connection.StateChange += (_, args) =>
        {
            if (args.CurrentState == ConnectionState.Open)
            {
                try
                {
                    _walPressure.Register(connection);
                    SqliteStorageMaintenance.ConfigureConnection(connection, _maximumDatabaseBytes);
                    TryRelieveWalPressure(connection);
                }
                catch (Exception exception)
                {
                    // A StateChange handler runs after the provider's Open cleanup block.
                    // Never leave a partially configured handle available to a subsequent retry.
                    connection.Close();
                    if (exception is SqliteException sqlite && SqliteCapacityError.IsCapacityFailure(sqlite))
                        throw SqliteCapacityError.Wrap(sqlite);
                    throw;
                }
            }
        };
        return connection;
    }
}
