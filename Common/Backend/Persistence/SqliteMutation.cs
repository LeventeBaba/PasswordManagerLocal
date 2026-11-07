using System.Security.Cryptography;
using Microsoft.Data.Sqlite;
using PasswordManagerLocal.Common.Backend.Abstractions.Persistence;
using PasswordManagerLocal.Common.Backend.Utils;

namespace PasswordManagerLocal.Common.Backend.Persistence;

/// <summary>One staged row write with its own snapshots of bound values.</summary>
internal sealed class SqliteMutation : IDisposable
{
    private readonly string _sql;
    private readonly (string Name, object? Value)[] _parameters;

    public enum Operation
    {
        Insert,
        Update,
        Delete
    }

    public SqliteMutation(
        Type rowType,
        Operation operation,
        Guid? userId,
        BackendChangeSet? changes,
        string sql,
        (string Name, object? Value)[] parameters)
    {
        RowType = rowType;
        Kind = operation;
        UserId = userId;
        _sql = sql;
        _parameters = parameters.Select(parameter => (parameter.Name, Snapshot(parameter.Value))).ToArray();
        if (changes is { IsEmpty: false })
        {
            Changes = new BackendChangeSet();
            Changes.Merge(changes);
        }
    }

    public Type RowType { get; }
    public Operation Kind { get; }
    public Guid? UserId { get; }
    public BackendChangeSet? Changes { get; }
    public object? Row { get; init; }
    public Action<bool>? Settle { get; init; }

    public async Task ExecuteAsync(SqliteConnection connection, SqliteTransaction transaction, CancellationToken ct)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = _sql;
        foreach (var (name, value) in _parameters)
            command.Parameters.AddWithValue(name, value ?? DBNull.Value);

        try
        {
            ct.ThrowIfCancellationRequested();
            var affectedRows = await command.ExecuteNonQueryAsync(ct);
            if (affectedRows == 0 && Kind is Operation.Update or Operation.Delete)
                throw new StorageWriteException(StorageWriteException.FailureKind.StaleRow, RowType);
            if (affectedRows != 1)
                throw new InvalidOperationException("A staged row mutation must affect exactly one row.");
        }
        catch (SqliteException exception) when (exception.SqliteExtendedErrorCode is 1555 or 2067 or 2579)
        {
            // PRIMARYKEY, UNIQUE, ROWID. The caller decides whether this is a recoverable race.
            throw new StorageWriteException(StorageWriteException.FailureKind.UniqueKeyConflict, RowType, exception);
        }
        catch (SqliteException exception) when (exception.SqliteErrorCode is 5 or 6)
        {
            throw new StorageWriteException(StorageWriteException.FailureKind.Busy, RowType, exception);
        }
    }

    public void Dispose()
    {
        foreach (var (_, value) in _parameters)
        {
            if (value is byte[] bytes)
                CryptographicOperations.ZeroMemory(bytes);
        }
    }

    private static object? Snapshot(object? value) =>
        value switch
        {
            byte[] bytes => bytes.ToArray(),
            DateTime timestamp => UtcDateTimeUtil.ToUtc(timestamp),
            DateTimeOffset timestamp => UtcDateTimeUtil.ToUtc(timestamp),
            _ => value
        };
}
