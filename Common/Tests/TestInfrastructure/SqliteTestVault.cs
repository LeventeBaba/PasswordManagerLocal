using Microsoft.Data.Sqlite;
using PasswordManagerLocal.Common.Backend.Persistence;
using SQLitePCL;

namespace PasswordManagerLocal.Common.Tests.TestInfrastructure;

/// <summary>Keeps a shared in-memory vault available across independent SQLite sessions.</summary>
internal sealed class SqliteTestVault : IAsyncDisposable
{
    private readonly SqliteConnection _anchor;

    static SqliteTestVault() => Batteries_V2.Init();

    private SqliteTestVault(SqliteConnection anchor, SqliteConnectionFactory connections)
    {
        _anchor = anchor;
        Connections = connections;
    }

    public SqliteConnectionFactory Connections { get; }

    public static async Task<SqliteTestVault> CreateAsync()
    {
        var connections = new SqliteConnectionFactory($"Data Source=TestVault-{Guid.NewGuid():N};Mode=Memory;Cache=Shared;Pooling=False");
        var anchor = connections.CreateConnection(enforceForeignKeys: true);
        try
        {
            await anchor.OpenAsync();
            await AppDatabaseInitializer.InitializeAsync(anchor);
            return new SqliteTestVault(anchor, connections);
        }
        catch
        {
            await anchor.DisposeAsync();
            throw;
        }
    }

    public ValueTask DisposeAsync() => _anchor.DisposeAsync();
}
