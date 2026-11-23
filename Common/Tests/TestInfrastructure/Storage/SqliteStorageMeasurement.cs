using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using PasswordManagerLocal.Common.Backend.Configuration;
using PasswordManagerLocal.Common.Backend.Persistence;
using System.Text.Json;

namespace PasswordManagerLocal.Common.Tests.TestInfrastructure.Storage;

/// <summary>Secret-free baseline data. File lengths alone do not establish a storage bound.</summary>
internal sealed record SqliteStorageMeasurement(
    long MainBytes, long WalBytes, long ShmBytes, long JournalBytes,
    long PageSize, long PageCount, long FreelistCount, string JournalMode,
    IReadOnlyDictionary<string, long> Rows, IReadOnlyDictionary<string, long> BlobBytes)
{
    public long AllocatedPageBytes => PageSize * PageCount;
    public long UsedPageBytes => PageSize * (PageCount - FreelistCount);
    public IReadOnlyDictionary<string, long> ChargedBytes { get; init; } = new Dictionary<string, long>();
    public IReadOnlyDictionary<string, long> CapacityBytes { get; init; } = new Dictionary<string, long>();
    public long MaximumPageCount { get; init; }
    public long AutoCheckpointPages { get; init; }
    public long RetainedJournalLimitBytes { get; init; }
    public long WalPressureLimitBytes { get; init; }

    public static Task<SqliteStorageMeasurement> CaptureAsync(ProductionSyncTestHost host, CancellationToken ct = default) =>
        CaptureAsync(host.Services.GetRequiredService<SqliteConnectionFactory>(),
            host.Services.GetRequiredService<BackendStoragePaths>().DatabasePath, ct);

    public static async Task<SqliteStorageMeasurement> CaptureAsync(SqliteConnectionFactory factory,
        string databasePath, CancellationToken ct = default)
    {
        await using var connection = factory.CreateConnection();
        await connection.OpenAsync(ct);
        using var transaction = connection.BeginTransaction(deferred: true);
        var names = new List<string>();
        using (var command = connection.CreateCommand())
        {
            command.Transaction = transaction;
            command.CommandText = "SELECT name FROM sqlite_schema WHERE type = 'table' AND name NOT LIKE 'sqlite_%' ORDER BY name;";
            using var reader = await command.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct)) names.Add(reader.GetString(0));
        }
        var counts = new SortedDictionary<string, long>(StringComparer.Ordinal);
        var blobs = new SortedDictionary<string, long>(StringComparer.Ordinal);
        foreach (var name in names)
        {
            var identifier = "\"" + name.Replace("\"", "\"\"") + "\"";
            using var command = connection.CreateCommand();
            command.Transaction = transaction;
            // Only names obtained from sqlite_schema are used; quote identifiers even in tests.
            command.CommandText = "SELECT COUNT(*) FROM " + identifier + ";";
            counts.Add(name, Convert.ToInt64(await command.ExecuteScalarAsync(ct)));
            var columns = new List<string>();
            command.CommandText = "PRAGMA table_info(" + identifier + ");";
            using (var reader = await command.ExecuteReaderAsync(ct))
            {
                while (await reader.ReadAsync(ct))
                    if (string.Equals(reader.GetString(2), "BLOB", StringComparison.OrdinalIgnoreCase))
                        columns.Add("COALESCE(length(\"" + reader.GetString(1).Replace("\"", "\"\"") + "\"), 0)");
            }
            if (columns.Count != 0)
            {
                command.CommandText = "SELECT COALESCE(SUM(" + string.Join(" + ", columns) + "), 0) FROM " + identifier + ";";
                blobs.Add(name, Convert.ToInt64(await command.ExecuteScalarAsync(ct)));
            }
        }
        var pageSize = await ReadPragmaAsync(connection, transaction, "page_size", ct);
        var pages = await ReadPragmaAsync(connection, transaction, "page_count", ct);
        var free = await ReadPragmaAsync(connection, transaction, "freelist_count", ct);
        string mode;
        using (var command = connection.CreateCommand())
        {
            command.Transaction = transaction;
            command.CommandText = "PRAGMA journal_mode;";
            mode = Convert.ToString(await command.ExecuteScalarAsync(ct)) ?? "unknown";
        }
        var charged = new SortedDictionary<string, long>(StringComparer.Ordinal);
        var capacities = new SortedDictionary<string, long>(StringComparer.Ordinal);
        using (var command = connection.CreateCommand())
        {
            command.Transaction = transaction;
            command.CommandText = "SELECT Category, UsedBytes, LimitBytes FROM StorageBudgets ORDER BY Category;";
            using var reader = await command.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct))
            {
                charged.Add(reader.GetString(0), reader.GetInt64(1));
                capacities.Add(reader.GetString(0), reader.GetInt64(2));
            }
        }
        var maximumPages = await ReadPragmaAsync(connection, transaction, "max_page_count", ct);
        var autoCheckpointPages = await ReadPragmaAsync(connection, transaction, "wal_autocheckpoint", ct);
        var journalLimit = await ReadPragmaAsync(connection, transaction, "journal_size_limit", ct);
        transaction.Commit();
        // Sample before this observer closes its connection: closing the final WAL connection
        // can checkpoint/remove sidecars. Callers must quiesce writers before comparing samples.
        return new(Size(databasePath), Size(databasePath + "-wal"), Size(databasePath + "-shm"),
            Size(databasePath + "-journal"), pageSize, pages, free, mode, counts, blobs)
        {
            ChargedBytes = charged, CapacityBytes = capacities, MaximumPageCount = maximumPages,
            AutoCheckpointPages = autoCheckpointPages, RetainedJournalLimitBytes = journalLimit,
            WalPressureLimitBytes = factory.WalPressureThresholdBytes
        };
    }

    public static Task<(long Busy, long LogFrames, long CheckpointedFrames)> CheckpointAsync(
        ProductionSyncTestHost host, CancellationToken ct = default) =>
        CheckpointAsync(host.Services.GetRequiredService<SqliteConnectionFactory>(), ct: ct);

    public static async Task<(long Busy, long LogFrames, long CheckpointedFrames)> CheckpointAsync(
        SqliteConnectionFactory factory, bool truncate = false, CancellationToken ct = default)
    {
        await using var connection = factory.CreateConnection();
        await connection.OpenAsync(ct);
        using var command = connection.CreateCommand();
        command.CommandText = truncate ? "PRAGMA wal_checkpoint(TRUNCATE);" : "PRAGMA wal_checkpoint(PASSIVE);";
        using var reader = await command.ExecuteReaderAsync(ct);
        if (!await reader.ReadAsync(ct)) throw new InvalidOperationException("Checkpoint returned no status.");
        return (reader.GetInt64(0), reader.GetInt64(1), reader.GetInt64(2));
    }

    public void WriteTo(TestContext context, string label) =>
        context.WriteLine(JsonSerializer.Serialize(new { Label = label, Measurement = this }));

    public void AssertSameRows(SqliteStorageMeasurement actual, params string[] tables)
    {
        foreach (var table in tables.Length == 0 ? Rows.Keys.ToArray() : tables)
        {
            Assert.IsTrue(actual.Rows.ContainsKey(table), $"Missing table: {table}");
            Assert.AreEqual(Rows[table], actual.Rows[table], $"Persistent row count changed: {table}");
        }
    }

    private static long Size(string path) => File.Exists(path) ? new FileInfo(path).Length : 0;
    private static async Task<long> ReadPragmaAsync(SqliteConnection connection, SqliteTransaction transaction,
        string name, CancellationToken ct)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "PRAGMA " + name + ";";
        return Convert.ToInt64(await command.ExecuteScalarAsync(ct));
    }
}
