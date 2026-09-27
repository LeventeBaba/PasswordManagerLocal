using Microsoft.EntityFrameworkCore;
using PasswordManagerLocal.Common.Backend.Abstractions.Services;
using PasswordManagerLocal.Common.Backend.Models;
using PasswordManagerLocal.Common.Backend.Persistence;
using System.Data;

namespace PasswordManagerLocal.Common.Backend.Services;

public sealed class DatabaseHealthService : IDatabaseHealthService
{
    private readonly AppDbContext _db;

    public DatabaseHealthService(AppDbContext db) => _db = db;

    public async Task<DatabaseHealthCheckResult> CheckAsync(CancellationToken ct = default)
    {
        var connection = _db.Database.GetDbConnection();
        var openedHere = connection.State != ConnectionState.Open;
        if (openedHere)
            await connection.OpenAsync(ct);

        try
        {
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

            return rowCount == 1 && healthy
                ? new DatabaseHealthCheckResult(true, "sqlite-quick-check-ok")
                : new DatabaseHealthCheckResult(false, "sqlite-quick-check-failed");
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch
        {
            return new DatabaseHealthCheckResult(false, "sqlite-quick-check-error");
        }
        finally
        {
            if (openedHere)
                await connection.CloseAsync();
        }
    }
}
