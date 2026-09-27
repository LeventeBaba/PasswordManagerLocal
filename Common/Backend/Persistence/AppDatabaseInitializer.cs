using Microsoft.EntityFrameworkCore;
using System.Data;

namespace PasswordManagerLocal.Common.Backend.Persistence;

internal static class AppDatabaseInitializer
{
    public static async Task InitializeAsync(AppDbContext db, CancellationToken ct = default)
    {
        await db.Database.EnsureCreatedAsync(ct);
        await RunQuickCheckAsync(db, ct);
    }

    private static async Task RunQuickCheckAsync(AppDbContext db, CancellationToken ct)
    {
        var connection = db.Database.GetDbConnection();
        var openedHere = connection.State != ConnectionState.Open;
        if (openedHere)
            await connection.OpenAsync(ct);

        try
        {
            await using var command = connection.CreateCommand();
            command.CommandText = "PRAGMA quick_check;";
            await using var reader = await command.ExecuteReaderAsync(ct);
            var rows = new List<string>();
            while (await reader.ReadAsync(ct))
                rows.Add(reader.IsDBNull(0) ? string.Empty : reader.GetString(0));

            if (rows.Count != 1 || !string.Equals(rows[0], "ok", StringComparison.OrdinalIgnoreCase))
            {
                var diagnostic = rows.Count == 0 ? "no-result" : string.Join(";", rows.Take(8));
                throw new InvalidDataException(
                    $"Database structural integrity verification failed ({diagnostic}). Normal writes are disabled.");
            }
        }
        finally
        {
            if (openedHere)
                await connection.CloseAsync();
        }
    }
}
