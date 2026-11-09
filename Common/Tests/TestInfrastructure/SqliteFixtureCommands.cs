using PasswordManagerLocal.Common.Backend.Persistence;

namespace PasswordManagerLocal.Common.Tests.TestInfrastructure;

/// <summary>Executes deliberate fixture edits through the scoped SQLite connection.</summary>
internal static class SqliteFixtureCommands
{
    public static Task<int> ExecuteFixtureSqlAsync(this IServiceProvider services, string sql,
        params (string Name, object? Value)[] parameters) =>
        services.GetRequiredService<SqliteSession>().ExecuteFixtureSqlAsync(sql, parameters);

    public static async Task<int> ExecuteFixtureSqlAsync(this SqliteSession session, string sql,
        params (string Name, object? Value)[] parameters)
    {
        using var command = await session.CreateCommandAsync(sql);
        foreach (var (name, value) in parameters)
            command.Parameters.AddWithValue(name, value ?? DBNull.Value);
        return await command.ExecuteNonQueryAsync();
    }
}
