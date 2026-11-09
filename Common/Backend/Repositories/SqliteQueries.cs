using Microsoft.Data.Sqlite;
using PasswordManagerLocal.Common.Backend.Persistence;
using PasswordManagerLocal.Common.Backend.Utils;

namespace PasswordManagerLocal.Common.Backend.Repositories;

internal static class SqliteQueries
{
    private const int IdBatchSize = 256;

    public static async Task<List<T>> ReadAsync<T>(
        this SqliteSession session,
        string sql,
        Func<SqliteDataReader, T> read,
        CancellationToken ct,
        params (string Name, object? Value)[] parameters)
    {
        using var command = await session.CreateCommandAsync(sql, ct);
        Bind(command, parameters);
        using var reader = await command.ExecuteReaderAsync(ct);
        var rows = new List<T>();
        while (await reader.ReadAsync(ct))
        {
            ct.ThrowIfCancellationRequested();
            rows.Add(read(reader));
        }
        return rows;
    }

    public static async Task<bool> ExistsAsync(
        this SqliteSession session,
        string select,
        CancellationToken ct,
        params (string Name, object? Value)[] parameters)
    {
        using var command = await session.CreateCommandAsync($"SELECT EXISTS ({select});", ct);
        Bind(command, parameters);
        return (long)(await command.ExecuteScalarAsync(ct))! != 0;
    }

    public static async Task<List<T>> ReadByIdsAsync<T>(
        this SqliteSession session,
        IReadOnlyCollection<Guid> ids,
        string sql,
        Func<SqliteDataReader, T> read,
        CancellationToken ct,
        params (string Name, object? Value)[] parameters)
    {
        var rows = new List<T>();
        foreach (var batch in ids.Distinct().Chunk(IdBatchSize))
        {
            var bound = new (string Name, object? Value)[batch.Length];
            for (var index = 0; index < batch.Length; index++)
                bound[index] = ($"$id{index}", batch[index]);

            var statement = sql.Replace("{ids}", string.Join(", ", bound.Select(parameter => parameter.Name)), StringComparison.Ordinal);
            rows.AddRange(await session.ReadAsync(statement, read, ct, [.. parameters, .. bound]));
        }
        return rows;
    }

    public static byte[] Blob(this SqliteDataReader row, int ordinal) => row.GetFieldValue<byte[]>(ordinal);

    public static byte[]? NullableBlob(this SqliteDataReader row, int ordinal) =>
        row.IsDBNull(ordinal) ? null : row.Blob(ordinal);

    public static Guid? NullableGuid(this SqliteDataReader row, int ordinal) =>
        row.IsDBNull(ordinal) ? null : row.GetGuid(ordinal);

    public static long? NullableInt64(this SqliteDataReader row, int ordinal) =>
        row.IsDBNull(ordinal) ? null : row.GetInt64(ordinal);

    public static DateTime UtcDateTime(this SqliteDataReader row, int ordinal) =>
        UtcDateTimeUtil.ToUtc(row.GetDateTime(ordinal));

    public static DateTimeOffset UtcOffset(this SqliteDataReader row, int ordinal) =>
        UtcDateTimeUtil.ToUtc(row.GetDateTimeOffset(ordinal));

    public static DateTimeOffset? NullableUtcOffset(this SqliteDataReader row, int ordinal) =>
        row.IsDBNull(ordinal) ? null : row.UtcOffset(ordinal);

    public static string? NullableText(this SqliteDataReader row, int ordinal) =>
        row.IsDBNull(ordinal) ? null : row.GetString(ordinal);

    private static void Bind(SqliteCommand command, (string Name, object? Value)[] parameters)
    {
        foreach (var (name, value) in parameters)
            command.Parameters.AddWithValue(name, value ?? DBNull.Value);
    }
}
