using Microsoft.Data.Sqlite;

namespace PasswordManagerLocal.Common.Tests.TestInfrastructure.Storage;

/// <summary>Raw rows exercise structural admission, separately from authenticated service tests.</summary>
internal static class SqliteStorageTestCommands
{
    public static async Task InsertRowAsync(SqliteConnection connection, string table, Guid id,
        IReadOnlyDictionary<string, object>? overrides = null)
    {
        var columns = new List<(string Name, string Type)>();
        using (var schema = connection.CreateCommand())
        {
            schema.CommandText = "PRAGMA table_info(" + Quote(table) + ");";
            using var reader = await schema.ExecuteReaderAsync();
            while (await reader.ReadAsync()) columns.Add((reader.GetString(1), reader.GetString(2)));
        }
        using var command = connection.CreateCommand();
        command.CommandText = "INSERT INTO " + Quote(table) + " (" + string.Join(",", columns.Select(c => Quote(c.Name))) +
            ") VALUES (" + string.Join(",", columns.Select((_, index) => "$v" + index)) + ");";
        for (var index = 0; index < columns.Count; index++)
        {
            var (name, type) = columns[index];
            object value = type switch
            {
                "TEXT" => id.ToString(),
                "BLOB" when name == "AttestedReceiptHash" => Array.Empty<byte>(),
                "BLOB" => new byte[32],
                _ => 1L
            };
            if (overrides is not null && overrides.TryGetValue(name, out var replacement)) value = replacement;
            command.Parameters.AddWithValue("$v" + index, value);
        }
        await command.ExecuteNonQueryAsync();
    }

    public static async Task<long> ScalarAsync(SqliteConnection connection, string sql)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        return Convert.ToInt64(await command.ExecuteScalarAsync());
    }

    public static async Task ExecuteAsync(SqliteConnection connection, string sql)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        await command.ExecuteNonQueryAsync();
    }

    public static async Task SetBudgetAsync(SqliteConnection connection, string category, long bytes)
    {
        using var command = connection.CreateCommand();
        command.CommandText = "UPDATE StorageBudgets SET LimitBytes = $bytes WHERE Category = $category;";
        command.Parameters.AddWithValue("$bytes", bytes);
        command.Parameters.AddWithValue("$category", category);
        await command.ExecuteNonQueryAsync();
    }

    public static string Quote(string identifier) => "\"" + identifier.Replace("\"", "\"\"", StringComparison.Ordinal) + "\"";
}
