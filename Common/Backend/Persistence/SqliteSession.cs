using System.Data;
using Microsoft.Data.Sqlite;
using PasswordManagerLocal.Common.Backend.Abstractions.Notifications;
using PasswordManagerLocal.Common.Backend.Abstractions.Persistence;
using PasswordManagerLocal.Common.Backend.Models;

namespace PasswordManagerLocal.Common.Backend.Persistence;

/// <summary>A sequential use-case scope for direct SQLite repositories.</summary>
internal sealed class SqliteSession : IUnitOfWork, IDisposable
{
    private const string FlushSavepoint = "pml_flush";
    private readonly SqliteConnection _connection;
    private readonly IBackendChangePublisher? _publisher;
    private readonly List<SqliteMutation> _pending = [];
    private SessionTransaction? _active;
    private SqliteTransaction? _readSnapshot;
    private bool _disposed;

    public SqliteSession(SqliteConnectionFactory connections, IBackendChangePublisher? publisher = null)
    {
        _connection = connections.CreateConnection(enforceForeignKeys: true);
        _publisher = publisher;
    }

    public bool HasActiveTransaction => _active is not null;

    // Repository reads and audited immediate operations use the scope's transaction.
    public async Task<SqliteCommand> CreateCommandAsync(string sql, CancellationToken ct = default)
    {
        await EnsureOpenAsync(ct);
        var command = _connection.CreateCommand();
        command.Transaction = _active?.Native ?? _readSnapshot;
        command.CommandText = sql;
        return command;
    }

    public async Task<T> ReadSnapshotAsync<T>(Func<Task<T>> read, CancellationToken ct)
    {
        await EnsureOpenAsync(ct);
        if (_active is not null || _readSnapshot is not null)
            return await read();

        // A relationship graph spans several bounded queries and needs one consistent read view.
        using var transaction = _connection.BeginTransaction(deferred: true);
        _readSnapshot = transaction;
        try
        {
            return await read();
        }
        finally
        {
            _readSnapshot = null;
        }
    }

    public void Stage<TRow>(
        SqliteMutation.Operation operation,
        string sql,
        Guid? userId,
        BackendChangeSet? changes,
        params (string Name, object? Value)[] parameters)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        var rowType = typeof(TRow);
        RequireAccountId(rowType, operation, userId);
        _pending.Add(new SqliteMutation(rowType, operation, userId, changes, sql, parameters));
    }

    public IEnumerable<(T Row, SqliteMutation.Operation Operation)> GetPendingRows<T>() where T : class
    {
        foreach (var mutation in _pending)
        {
            if (mutation.Row is T row)
                yield return (row, mutation.Kind);
        }
    }

    public SqliteMutation.Operation? GetPendingOperation(object row) =>
        _pending.Find(mutation => ReferenceEquals(mutation.Row, row))?.Kind;

    public void DiscardPendingRow(object row)
    {
        var index = _pending.FindIndex(mutation => ReferenceEquals(mutation.Row, row));
        if (index < 0)
            return;

        _pending[index].Dispose();
        _pending.RemoveAt(index);
    }

    public void StageRow(SqliteMutation mutation)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(mutation.Row);
        RequireAccountId(mutation.RowType, mutation.Kind, mutation.UserId);
        var index = _pending.FindIndex(pending => ReferenceEquals(pending.Row, mutation.Row));
        if (index < 0)
            _pending.Add(mutation);
        else
        {
            _pending[index].Dispose();
            _pending[index] = mutation;
        }
    }

    public async Task<IUnitOfWorkTransaction> BeginTransactionAsync(CancellationToken ct = default) =>
        await BeginTransactionAsync(discardPendingOnRollback: true, ct);

    public async Task<int> SaveChangesAsync(CancellationToken ct = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ct.ThrowIfCancellationRequested();
        var count = _pending.Count;
        if (count == 0)
            return 0;

        var changes = new BackendChangeSet();
        try
        {
            if (_active is { } active)
            {
                await FlushWithSavepointAsync(active.Native, changes, ct);
                active.Changes.Merge(changes);
                AcceptPending(active);
            }
            else
            {
                using (var transaction = await BeginTransactionAsync(discardPendingOnRollback: false, ct))
                {
                    await FlushAsync(transaction.Native, changes, ct);
                    await transaction.CommitAsync(ct);
                    AcceptPending(transaction: null);
                }
                changes.Publish(_publisher);
            }
        }
        catch (SqliteException exception) when (IsBusy(exception))
        {
            throw new StorageWriteException(StorageWriteException.FailureKind.Busy, innerException: exception);
        }

        return count;
    }

    public void ClearTrackedChanges()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ClearPending();
    }

    public void Dispose()
    {
        if (_disposed)
            return;

        _disposed = true;
        try
        {
            _active?.Dispose();
        }
        finally
        {
            ClearPending();
            _connection.Dispose();
        }
    }

    private async Task<SessionTransaction> BeginTransactionAsync(bool discardPendingOnRollback, CancellationToken ct)
    {
        if (_active is not null || _readSnapshot is not null)
            throw new InvalidOperationException("A unit-of-work transaction is already active.");

        IDisposable? lease = null;
        try
        {
            await EnsureOpenAsync(ct);
            lease = AppDatabaseTransactionRegistry.Enter(AppDatabaseTransactionRegistry.GetDatabaseKey(_connection));
            var transaction = _connection.BeginTransaction(deferred: false);
            _active = new SessionTransaction(this, transaction, lease, discardPendingOnRollback);
            lease = null;
            return _active;
        }
        catch (SqliteException exception) when (IsBusy(exception))
        {
            throw new StorageWriteException(StorageWriteException.FailureKind.Busy, innerException: exception);
        }
        finally
        {
            lease?.Dispose();
        }
    }

    private async Task FlushWithSavepointAsync(SqliteTransaction transaction, BackendChangeSet changes, CancellationToken ct)
    {
        transaction.Save(FlushSavepoint);
        try
        {
            await FlushAsync(transaction, changes, ct);
            ct.ThrowIfCancellationRequested();
            transaction.Release(FlushSavepoint);
        }
        catch (Exception failure)
        {
            try
            {
                transaction.Rollback(FlushSavepoint);
                transaction.Release(FlushSavepoint);
            }
            catch (Exception rollbackFailure)
            {
                // SQLite may already have rolled back the entire transaction on a fatal error.
                // Retire this scope rather than risk committing work with an unknown rollback state.
                try { Dispose(); }
                catch (Exception disposeFailure)
                {
                    throw new AggregateException(failure, rollbackFailure, disposeFailure);
                }
                throw new AggregateException(failure, rollbackFailure);
            }
            throw;
        }
    }

    private async Task FlushAsync(SqliteTransaction transaction, BackendChangeSet changes, CancellationToken ct)
    {
        await ValidateAccountWritesAsync(transaction, ct);
        foreach (var mutation in _pending)
        {
            await mutation.ExecuteAsync(_connection, transaction, ct);
            if (mutation.Changes is { } mutationChanges)
                changes.Merge(mutationChanges);
        }
    }

    private async Task ValidateAccountWritesAsync(SqliteTransaction transaction, CancellationToken ct)
    {
        HashSet<Guid> userIds = [];
        HashSet<Guid> stagedBarriers = [];
        foreach (var mutation in _pending)
        {
            if (mutation.Kind == SqliteMutation.Operation.Delete ||
                mutation.UserId is not Guid userId || userId == Guid.Empty)
                continue;

            if (mutation.RowType == typeof(User) || mutation.RowType == typeof(UserLoginIdentityState))
                userIds.Add(userId);
            else if (mutation.RowType == typeof(DeletedUserBarrier))
                stagedBarriers.Add(userId);
        }
        if (userIds.Count == 0)
            return;

        using var command = _connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """SELECT EXISTS (SELECT 1 FROM "DeletedUserBarriers" WHERE "UserId" = $userId);""";
        var parameter = command.Parameters.Add("$userId", SqliteType.Text);
        foreach (var userId in userIds)
        {
            ct.ThrowIfCancellationRequested();
            parameter.Value = userId;
            if (stagedBarriers.Contains(userId) || (long)(await command.ExecuteScalarAsync(ct))! != 0)
            {
                throw new InvalidOperationException(
                    "A permanent account-deletion barrier prevents recreating or updating this user identity.");
            }
        }
    }

    private async Task EnsureOpenAsync(CancellationToken ct)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ct.ThrowIfCancellationRequested();
        if (_connection.State != ConnectionState.Open)
            await _connection.OpenAsync(ct);
        ct.ThrowIfCancellationRequested();
    }

    private void AcceptPending(SessionTransaction? transaction)
    {
        foreach (var mutation in _pending)
        {
            if (mutation.Settle is not { } settle)
                continue;

            settle(true);
            transaction?.RegisterRollback(settle);
        }
        ClearPending();
    }

    private static void RequireAccountId(Type rowType, SqliteMutation.Operation operation, Guid? userId)
    {
        if (operation != SqliteMutation.Operation.Delete && userId is null &&
            (rowType == typeof(User) || rowType == typeof(UserLoginIdentityState) || rowType == typeof(DeletedUserBarrier)))
        {
            throw new ArgumentNullException(nameof(userId), "This row write requires its account identity.");
        }
    }

    private void ClearPending()
    {
        foreach (var mutation in _pending)
            mutation.Dispose();
        _pending.Clear();
    }

    private static bool IsBusy(SqliteException exception) => exception.SqliteErrorCode is 5 or 6;

    private sealed class SessionTransaction(
        SqliteSession owner,
        SqliteTransaction transaction,
        IDisposable registryLease,
        bool discardPendingOnRollback) : IUnitOfWorkTransaction, IDisposable
    {
        private readonly List<Action<bool>> _rollbackActions = [];
        private bool _completed;

        public SqliteTransaction Native => transaction;
        public BackendChangeSet Changes { get; } = new();

        public void RegisterRollback(Action<bool> settle) => _rollbackActions.Add(settle);

        public async Task CommitAsync(CancellationToken ct = default)
        {
            if (_completed)
                throw new InvalidOperationException("The unit-of-work transaction has already completed.");
            ct.ThrowIfCancellationRequested();
            try
            {
                await transaction.CommitAsync(ct);
            }
            catch (SqliteException exception) when (IsBusy(exception))
            {
                // A busy COMMIT can be retried or rolled back by the caller.
                throw new StorageWriteException(StorageWriteException.FailureKind.Busy, innerException: exception);
            }

            Complete(committed: true);
        }

        public async Task RollbackAsync(CancellationToken ct = default)
        {
            if (_completed)
                return;

            ct.ThrowIfCancellationRequested();
            await transaction.RollbackAsync(ct);
            Complete(committed: false);
        }

        public ValueTask DisposeAsync()
        {
            Dispose();
            return ValueTask.CompletedTask;
        }

        public void Dispose()
        {
            if (!_completed)
                Complete(committed: false);
        }

        private void Complete(bool committed)
        {
            _completed = true;
            try
            {
                // Disposing a live provider transaction rolls back any uncommitted work.
                transaction.Dispose();
            }
            catch
            {
                // A failed rollback leaves the connection unsuitable for more use.
                owner.Dispose();
                throw;
            }
            finally
            {
                owner._active = null;
                registryLease.Dispose();
                if (!committed)
                {
                    for (var index = _rollbackActions.Count - 1; index >= 0; index--)
                        _rollbackActions[index](false);
                    if (discardPendingOnRollback)
                        owner.ClearPending();
                }
                _rollbackActions.Clear();
                if (committed)
                    Changes.Publish(owner._publisher);
            }
        }
    }
}
