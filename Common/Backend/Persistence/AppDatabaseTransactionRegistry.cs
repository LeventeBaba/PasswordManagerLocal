using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using System.Collections.Concurrent;
using System.Runtime.CompilerServices;

namespace PasswordManagerLocal.Common.Backend.Persistence;

/// <summary>
/// Tracks database transactions owned by this backend process. The durable synchronization clock
/// uses a separate SQLite connection, so without this registry a clock observation made from inside an
/// existing SQLite transaction can wait on the transaction that is itself waiting for Observe.
/// </summary>
internal static class AppDatabaseTransactionRegistry
{
    private static readonly ConcurrentDictionary<string, int> ActiveTransactions = new(StringComparer.Ordinal);

    public static string GetDatabaseKey(AppDbContext db)
    {
        ArgumentNullException.ThrowIfNull(db);
        var connection = db.Database.GetDbConnection();
        return connection is SqliteConnection sqlite
            ? GetDatabaseKey(sqlite)
            : connection.ConnectionString;
    }

    public static string GetDatabaseKey(SqliteConnection connection)
    {
        ArgumentNullException.ThrowIfNull(connection);
        // DataSource changes after opening a file URI. Use the configured value so
        // the clock and the unit of work agree before and after opening.
        var options = new SqliteConnectionStringBuilder(connection.ConnectionString);
        var dataSource = options.DataSource;
        if (string.IsNullOrWhiteSpace(dataSource) ||
            string.Equals(dataSource, ":memory:", StringComparison.OrdinalIgnoreCase))
        {
            return $"memory:{RuntimeHelpers.GetHashCode(connection)}";
        }

        if (options.Mode == SqliteOpenMode.Memory)
            return options.Cache == SqliteCacheMode.Shared
                ? $"shared-memory:{dataSource}"
                : $"memory:{RuntimeHelpers.GetHashCode(connection)}";

        if (dataSource.StartsWith("file:", StringComparison.OrdinalIgnoreCase) &&
            Uri.TryCreate(dataSource, UriKind.Absolute, out var uri) && uri.IsFile)
        {
            dataSource = uri.LocalPath;
        }

        return Path.GetFullPath(dataSource);
    }

    public static IDisposable Enter(string databaseKey)
    {
        if (string.IsNullOrWhiteSpace(databaseKey))
            throw new ArgumentException("A database key is required.", nameof(databaseKey));

        ActiveTransactions.AddOrUpdate(databaseKey, 1, static (_, current) => checked(current + 1));
        return new Lease(databaseKey);
    }

    public static bool HasActiveTransaction(string databaseKey) =>
        !string.IsNullOrWhiteSpace(databaseKey) &&
        ActiveTransactions.TryGetValue(databaseKey, out var count) &&
        count > 0;

    private static void Exit(string databaseKey)
    {
        while (ActiveTransactions.TryGetValue(databaseKey, out var current))
        {
            if (current <= 1)
            {
                if (((ICollection<KeyValuePair<string, int>>)ActiveTransactions)
                    .Remove(new KeyValuePair<string, int>(databaseKey, current)))
                {
                    return;
                }
            }
            else if (ActiveTransactions.TryUpdate(databaseKey, current - 1, current))
            {
                return;
            }
        }
    }

    private sealed class Lease : IDisposable
    {
        private readonly string _databaseKey;
        private int _disposed;

        public Lease(string databaseKey) => _databaseKey = databaseKey;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 0)
                Exit(_databaseKey);
        }
    }
}
