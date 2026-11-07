using System.Runtime.CompilerServices;
using PasswordManagerLocal.Common.Backend.Persistence;

namespace PasswordManagerLocal.Common.Backend.Repositories.Sqlite;

/// <summary>Stages explicit writes for one table and retains original values on materialized rows.</summary>
internal sealed class SqliteRowWriter<T>(
    SqliteSession session,
    string table,
    int keyCount,
    (string Column, Func<T, object?> GetValue)[] fields,
    string? concurrencyColumn = null,
    Func<T, Guid?>? userId = null) where T : class
{
    private readonly ConditionalWeakTable<T, object?[]> _originals = new();

    public T Remember(T row)
    {
        SetOriginal(row, Snapshot(row));
        return row;
    }

    public void Stage(T row, SqliteMutation.Operation operation)
    {
        var pending = session.GetPendingOperation(row);
        if (pending == SqliteMutation.Operation.Insert && operation == SqliteMutation.Operation.Delete)
        {
            session.DiscardPendingRow(row);
            return;
        }
        if (pending == SqliteMutation.Operation.Insert && operation == SqliteMutation.Operation.Update)
            operation = SqliteMutation.Operation.Insert;

        _originals.TryGetValue(row, out var original);
        var values = Snapshot(row);
        if (original is not null && !original.Take(keyCount).SequenceEqual(values.Take(keyCount)))
            throw new InvalidOperationException("A persisted row's key cannot be changed.");

        var bindings = fields.Select((field, index) => (Name: $"$p{index}", Value: values[index])).ToList();
        var predicate = string.Join(" AND ", fields.Take(keyCount).Select((field, index) => $"\"{field.Column}\" = $p{index}"));
        if (operation != SqliteMutation.Operation.Insert && concurrencyColumn is not null)
        {
            var index = Array.FindIndex(fields, field => field.Column == concurrencyColumn);
            if (original is null)
                throw new InvalidOperationException("A version-checked row must be read in the write scope before it can be changed.");
            predicate += $" AND \"{concurrencyColumn}\" = $original";
            bindings.Add(("$original", original[index]));
        }

        var sql = operation switch
        {
            SqliteMutation.Operation.Insert =>
                $"INSERT INTO \"{table}\" ({string.Join(", ", fields.Select(field => $"\"{field.Column}\""))}) " +
                $"VALUES ({string.Join(", ", bindings.Select(binding => binding.Name))});",
            SqliteMutation.Operation.Update =>
                $"UPDATE \"{table}\" SET {string.Join(", ", fields.Skip(keyCount).Select((field, index) => $"\"{field.Column}\" = $p{index + keyCount}"))} WHERE {predicate};",
            SqliteMutation.Operation.Delete => $"DELETE FROM \"{table}\" WHERE {predicate};",
            _ => throw new ArgumentOutOfRangeException(nameof(operation))
        };

        // SQLite rejects parameters absent from the command in some provider paths.
        if (operation == SqliteMutation.Operation.Delete)
            bindings.RemoveRange(keyCount, fields.Length - keyCount);

        var changes = new BackendChangeSet();
        changes.AddEntityMutation(row, operation != SqliteMutation.Operation.Update, property =>
        {
            var index = Array.FindIndex(fields, field => field.Column == property);
            return index >= 0 && (original is null || !SameValue(original[index], values[index]));
        });

        var accepted = operation == SqliteMutation.Operation.Delete ? null : values;
        var mutation = new SqliteMutation(typeof(T), operation, userId?.Invoke(row), changes, sql, bindings.ToArray())
        {
            Row = row,
            Settle = applied => SetOriginal(row, applied ? accepted : original)
        };
        try
        {
            session.StageRow(mutation);
        }
        catch
        {
            mutation.Dispose();
            throw;
        }
    }

    private object?[] Snapshot(T row) => fields.Select(field =>
    {
        var value = field.GetValue(row);
        return value is byte[] blob ? blob.ToArray() : value;
    }).ToArray();

    private void SetOriginal(T row, object?[]? values)
    {
        _originals.Remove(row);
        if (values is not null)
            _originals.Add(row, values);
    }

    private static bool SameValue(object? original, object? value) =>
        original is byte[] before && value is byte[] after
            ? before.AsSpan().SequenceEqual(after)
            : Equals(original, value);
}
