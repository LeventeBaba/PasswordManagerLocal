using System.Runtime.CompilerServices;
using PasswordManagerLocal.Common.Backend.Persistence;

namespace PasswordManagerLocal.Common.Backend.Repositories.Sqlite;

/// <summary>Stages explicit writes for one table and retains original values on materialized rows.</summary>
internal sealed class SqliteRowWriter<T>(
    SqliteSession session,
    string table,
    int keyCount,
    (string Column, Func<T, object?> GetValue)[] fields,
    string[]? concurrencyColumns = null,
    Func<T, Guid?>? userId = null,
    string? targetedColumn = null,
    bool trackChanges = true) where T : class
{
    private readonly ConditionalWeakTable<T, object?[]> _originals = new();

    public T Remember(T row)
    {
        SetOriginal(row, SnapshotOriginal(ReadValues(row)));
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
        var values = ReadValues(row);
        if (original is not null && !original.Take(keyCount).SequenceEqual(values.Take(keyCount)))
            throw new InvalidOperationException("A persisted row's key cannot be changed.");

        var bindings = new List<(string Name, object? Value)>();
        var assignments = new List<string>();
        var predicate = string.Join(" AND ", fields.Take(keyCount).Select((field, index) => $"\"{field.Column}\" = $p{index}"));
        for (var index = 0; index < fields.Length; index++)
        {
            var column = fields[index].Column;
            if (operation == SqliteMutation.Operation.Delete && index >= keyCount ||
                operation == SqliteMutation.Operation.Update && column == targetedColumn && original is not null &&
                SameValue(original[index], values[index]))
            {
                continue;
            }

            var parameter = $"$p{index}";
            bindings.Add((parameter, values[index]));
            if (operation == SqliteMutation.Operation.Update && index >= keyCount)
                assignments.Add($"\"{column}\" = {parameter}");
        }

        var sql = operation switch
        {
            SqliteMutation.Operation.Insert =>
                $"INSERT INTO \"{table}\" ({string.Join(", ", fields.Select(field => $"\"{field.Column}\""))}) " +
                $"VALUES ({string.Join(", ", bindings.Select(binding => binding.Name))});",
            SqliteMutation.Operation.Update =>
                $"UPDATE \"{table}\" SET {string.Join(", ", assignments)} WHERE {predicate}",
            SqliteMutation.Operation.Delete => $"DELETE FROM \"{table}\" WHERE {predicate}",
            _ => throw new ArgumentOutOfRangeException(nameof(operation))
        };

        if (operation != SqliteMutation.Operation.Insert && concurrencyColumns is not null)
        {
            if (original is null)
                throw new InvalidOperationException("A version-checked row must be read in the write scope before it can be changed.");
            foreach (var column in concurrencyColumns)
            {
                var index = Array.FindIndex(fields, field => field.Column == column);
                var parameter = $"$original{index}";
                sql += $" AND \"{column}\" = {parameter}";
                bindings.Add((parameter, original[index]));
            }
        }

        BackendChangeSet? changes = null;
        if (trackChanges)
        {
            changes = new BackendChangeSet();
            changes.AddEntityMutation(row, operation != SqliteMutation.Operation.Update, property =>
            {
                var index = Array.FindIndex(fields, field => field.Column == property);
                return index >= 0 && (original is null || !SameValue(original[index], values[index]));
            });
        }

        var accepted = operation == SqliteMutation.Operation.Delete ? null : SnapshotOriginal(values);
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

    private object?[] ReadValues(T row) => fields.Select(field => field.GetValue(row)).ToArray();

    private object?[] SnapshotOriginal(object?[] values)
    {
        var snapshot = new object?[values.Length];
        for (var index = 0; index < values.Length; index++)
        {
            if (trackChanges || index < keyCount || concurrencyColumns?.Contains(fields[index].Column) == true)
                snapshot[index] = values[index] is byte[] blob ? blob.ToArray() : values[index];
        }
        return snapshot;
    }

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
