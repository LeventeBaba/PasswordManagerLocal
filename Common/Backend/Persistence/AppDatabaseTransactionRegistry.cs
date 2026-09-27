using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using System.Collections.Concurrent;
using System.Runtime.CompilerServices;

namespace PasswordManagerLocal.Common.Backend.Persistence;

/// <summary>
/// Tracks database transactions owned by this backend process. The durable synchronization clock
/// uses a separate DbContext, so without this registry a clock observation made from inside an
/// existing SQLite transaction can wait on the transaction that is itself waiting for Observe.
/// </summary>
internal static class AppDatabaseTransactionRegistry
{
    private static readonly ConcurrentDictionary<string, int> ActiveTransactions = new(StringComparer.Ordinal);

    public static string GetDatabaseKey(AppDbContext db)
    {
        ArgumentNullException.ThrowIfNull(db);
        var connection = db.Database.GetDbConnection();
        if (connection is SqliteConnection sqlite)
        {
            var dataSource = sqlite.DataSource;
            if (string.IsNullOrWhiteSpace(dataSource) || string.Equals(dataSource, ":memory:", StringComparison.OrdinalIgnoreCase))
                return $"memory:{RuntimeHelpers.GetHashCode(connection)}";

            return Path.GetFullPath(dataSource);
        }

        return connection.ConnectionString;
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
