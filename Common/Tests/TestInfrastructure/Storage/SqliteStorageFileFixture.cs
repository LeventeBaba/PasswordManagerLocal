using Microsoft.Data.Sqlite;
using PasswordManagerLocal.Common.Backend.Persistence;
using SQLitePCL;

namespace PasswordManagerLocal.Common.Tests.TestInfrastructure.Storage;

internal sealed class SqliteStorageFileFixture : IAsyncDisposable
{
    private readonly string _directory;
    private SqliteStorageFileFixture(string directory, SqliteConnectionFactory factory, SqliteConnection anchor)
    {
        _directory = directory; Factory = factory; Anchor = anchor;
        DatabasePath = Path.Combine(directory, "capacity.db");
    }
    public string DatabasePath { get; }
    public SqliteConnectionFactory Factory { get; }
    public SqliteConnection Anchor { get; }

    public static async Task<SqliteStorageFileFixture> CreateAsync(
        long walPressureBytes = PasswordManagerLocal.Common.Backend.Constants.StorageCapacityConstants.WalPressureBytes,
        TimeProvider? timeProvider = null,
        SqlitePhysicalLimits? physicalLimits = null)
    {
        Batteries_V2.Init();
        var directory = Path.Combine(Path.GetTempPath(), "PasswordManagerLocal.StorageTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var factory = new SqliteConnectionFactory(new SqliteConnectionStringBuilder
        {
            DataSource = Path.Combine(directory, "capacity.db"), Pooling = false, DefaultTimeout = 2
        }.ToString(), walPressureBytes, timeProvider, physicalLimits: physicalLimits);
        var anchor = factory.CreateConnection();
        try
        {
            await anchor.OpenAsync();
            await AppDatabaseInitializer.InitializeAsync(anchor);
            // Bootstrap DDL may itself exceed a deliberately small test threshold. Begin the
            // workload with an empty WAL after SQLite has committed the complete guard inventory.
            using (var checkpoint = anchor.CreateCommand())
            {
                checkpoint.CommandText = "PRAGMA wal_checkpoint(TRUNCATE);";
                using var result = await checkpoint.ExecuteReaderAsync();
                if (!await result.ReadAsync() || result.GetInt64(0) != 0)
                    throw new InvalidOperationException("The isolated storage fixture could not checkpoint its bootstrap WAL.");
            }
            return new(directory, factory, anchor);
        }
        catch
        {
            await anchor.DisposeAsync();
            Directory.Delete(directory, recursive: true);
            throw;
        }
    }

    public async ValueTask DisposeAsync()
    {
        await Anchor.DisposeAsync();
        Directory.Delete(_directory, recursive: true);
    }
}
