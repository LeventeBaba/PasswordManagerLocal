using Microsoft.Data.Sqlite;
using PasswordManagerLocal.Common.Backend.Abstractions.Services;
using PasswordManagerLocal.Common.Backend.Models;
using PasswordManagerLocal.Common.Backend.Persistence;

namespace PasswordManagerLocal.Common.Backend.Services;

internal sealed class DatabaseHealthService : IDatabaseHealthService
{
    private readonly SqliteConnectionFactory _connections;

    public DatabaseHealthService(SqliteConnectionFactory connections) => _connections = connections;

    public async Task<DatabaseHealthCheckResult> CheckAsync(CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        // Recovery checks the committed vault before it starts its write transaction.
        await using var connection = _connections.CreateConnection();
        try
        {
            await connection.OpenAsync(ct);
            await using var command = connection.CreateCommand();
            command.CommandText = "PRAGMA quick_check;";
            await using var reader = await command.ExecuteReaderAsync(ct);
            var rowCount = 0;
            var healthy = false;
            while (await reader.ReadAsync(ct))
            {
                rowCount++;
                if (rowCount == 1 && !reader.IsDBNull(0))
                    healthy = string.Equals(reader.GetString(0), "ok", StringComparison.OrdinalIgnoreCase);
                else
                    healthy = false;
            }

            ct.ThrowIfCancellationRequested();
            return rowCount == 1 && healthy
                ? new DatabaseHealthCheckResult(true, "sqlite-quick-check-ok")
                : new DatabaseHealthCheckResult(false, "sqlite-quick-check-failed");
        }
        catch (SqliteException exception) when (exception.SqliteErrorCode is 11 or 26)
        {
            // SQLite can report a damaged vault or an invalid key as CORRUPT or NOTADB.
            // Busy, permission and I/O errors propagate as storage failures.
            ct.ThrowIfCancellationRequested();
            return new DatabaseHealthCheckResult(false, "sqlite-quick-check-error");
        }
    }
}
