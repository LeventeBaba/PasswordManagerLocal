using SQLitePCL;

namespace PasswordManagerLocal.Common.Backend.Persistence;

/// <summary>Owns the SQLite3MC provider for the entire process, including backend restarts.</summary>
internal static class SqliteProviderInitializer
{
    private static readonly object Gate = new();
    private static bool _initialized;

    public static void EnsureInitialized()
    {
        lock (Gate)
        {
            if (_initialized) return;
            // The provider owns native callback delegates. Replacing it while a
            // connection is alive can collect a delegate SQLite still calls.
            Batteries_V2.Init();
            // Microsoft.Data.Sqlite may also initialize the bundle on first use.
            // Keep that path and any later callers on the same rooted provider.
            raw.FreezeProvider();
            _initialized = true;
        }
    }
}
