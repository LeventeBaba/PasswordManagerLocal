using Microsoft.Data.Sqlite;
using PasswordManagerLocal.Common.Backend.Constants;
using System.Data;

namespace PasswordManagerLocal.Common.Backend.Persistence;

/// <summary>Fail-closed row admission and opportunistic checkpointing, never file truncation.</summary>
internal sealed class SqliteWalPressure
{
    internal const string FunctionName = "pml_wal_admit";
    private readonly long _pressureBytes;
    private readonly TimeProvider _time;
    private readonly object _checkpointGate = new();
    private long _lastAttempt;
    private bool _attempted;
    internal long PressureBytes => _pressureBytes;

    public SqliteWalPressure(long pressureBytes = StorageCapacityConstants.WalPressureBytes, TimeProvider? time = null)
    {
        if (pressureBytes <= 0) throw new ArgumentOutOfRangeException(nameof(pressureBytes));
        _pressureBytes = pressureBytes;
        _time = time ?? TimeProvider.System;
    }

    public void Register(SqliteConnection connection)
    {
        var path = GetWalPath(connection);
        // A fresh callback is installed on every pooled reopen. No SQL or checkpoint may run
        // inside this function: the caller is already executing a SQLite writer statement.
        connection.CreateFunction(FunctionName,
            () => path is null || TryReadLength(path, out var bytes) && bytes < _pressureBytes);
    }

    // Call only outside this connection's read/write transactions and active readers.
    public void TryCheckpoint(SqliteConnection connection)
    {
        if (connection.State != ConnectionState.Open) return;
        if (!Monitor.TryEnter(_checkpointGate)) return;
        try
        {
            var now = _time.GetTimestamp();
            if (_attempted && _time.GetElapsedTime(_lastAttempt, now) < StorageCapacityConstants.WalPressureRetryInterval)
                return;
            _attempted = true;
            _lastAttempt = now;

            // Coalesce ordinary probes too. Frequent read queries must not stat the same WAL
            // repeatedly; the write callback always samples afresh and never uses this cooldown.
            var path = GetWalPath(connection);
            if (path is null || !TryReadLength(path, out var bytes) || bytes < _pressureBytes) return;

            using var command = connection.CreateCommand();
            command.CommandTimeout = 1;
            // SQLite returns a busy status row for a blocked TRUNCATE checkpoint. Do not wait
            // for readers, change their lifetime, or retry inside this maintenance attempt.
            command.CommandText = "PRAGMA busy_timeout;";
            var previousTimeout = Convert.ToInt64(command.ExecuteScalar());
            try
            {
                command.CommandText = "PRAGMA busy_timeout = 0;";
                command.ExecuteNonQuery();
                command.CommandText = "PRAGMA wal_checkpoint(TRUNCATE);";
                using var reader = command.ExecuteReader();
                // Consume SQLite's result but never infer durable application/delivery from it.
                if (reader.Read()) _ = reader.GetInt64(0);
            }
            finally
            {
                command.CommandText = "PRAGMA busy_timeout = " + previousTimeout.ToString(System.Globalization.CultureInfo.InvariantCulture) + ";";
                command.ExecuteNonQuery();
            }
        }
        catch (SqliteException)
        {
            // Busy, read-only and I/O failures leave the write guard in force. Reads and explicit
            // recovery remain available; a failed checkpoint never permits deleting live sidecars.
        }
        finally { Monitor.Exit(_checkpointGate); }
    }

    private static string? GetWalPath(SqliteConnection connection)
    {
        var options = new SqliteConnectionStringBuilder(connection.ConnectionString);
        if (options.Mode == SqliteOpenMode.Memory || options.DataSource == ":memory:") return null;
        // The provider resolves URI/escaped filenames through sqlite3_db_filename after open.
        var path = connection.DataSource;
        if (string.IsNullOrWhiteSpace(path))
            throw new InvalidOperationException("A file-backed vault requires a resolved database filename.");
        return path + "-wal";
    }

    private static bool TryReadLength(string path, out long length)
    {
        try { length = new FileInfo(path).Length; return true; }
        catch (Exception exception) when (exception is FileNotFoundException or DirectoryNotFoundException)
        {
            length = 0;
            return true;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or System.Security.SecurityException)
        {
            length = 0;
            return false;
        }
    }
}
