using Microsoft.Data.Sqlite;
using PasswordManagerLocal.Common.Backend.Abstractions.Security;
using PasswordManagerLocal.Common.Backend.Configuration;
using PasswordManagerLocal.Common.Backend.Security;

namespace PasswordManagerLocal.Common.Backend.Persistence;

internal sealed class SqliteConnectionFactory
{
    private readonly Lazy<string> _connectionString;

    public SqliteConnectionFactory(BackendStoragePaths paths, IKeyProtector keyProtector)
    {
        // Share the protected key lookup across bootstrap, EF and standalone callers
        // for this host generation. Startup still migrates before opening the vault.
        _connectionString = new(() =>
        {
            var password = DbConfigManager.GetOrCreateDatabasePassword(paths, keyProtector);
            return DatabaseEncryptionMigrator.CreateRuntimeConnectionString(paths.DatabasePath, password);
        });
    }

    internal SqliteConnectionFactory(string connectionString)
    {
        _connectionString = new(() => connectionString);
    }

    public string ConnectionString => _connectionString.Value;

    public string GetDatabasePassword() => new SqliteConnectionStringBuilder(ConnectionString).Password;

    public SqliteConnection CreateConnection(bool enforceForeignKeys = false)
    {
        if (!enforceForeignKeys)
            return new SqliteConnection(ConnectionString);

        var options = new SqliteConnectionStringBuilder(ConnectionString) { ForeignKeys = true };
        return new SqliteConnection(options.ToString());
    }
}
