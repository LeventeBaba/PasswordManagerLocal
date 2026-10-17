using PasswordManagerLocal.Common.Backend.Abstractions.Persistence;
using PasswordManagerLocal.Common.Backend.Abstractions.Notifications;

namespace PasswordManagerLocal.Common.Backend.Persistence;

public sealed class AppUnitOfWork : IUnitOfWork
{
    private readonly AppDbContext _db;
    private readonly IBackendChangePublisher? _changePublisher;
    private BackendChangeSet? _transactionChanges;

    public AppUnitOfWork(AppDbContext db, IBackendChangePublisher? changePublisher = null)
    {
        _db = db ?? throw new ArgumentNullException(nameof(db));
        _changePublisher = changePublisher;
    }

    public async Task<IUnitOfWorkTransaction> BeginTransactionAsync(CancellationToken ct = default)
    {
        if (_transactionChanges is not null)
            throw new InvalidOperationException("A unit-of-work transaction is already active.");

        var transaction = await _db.Database.BeginTransactionAsync(ct);
        var changes = new BackendChangeSet();
        _transactionChanges = changes;
        try
        {
            return new AppUnitOfWorkTransaction(
                transaction,
                AppDatabaseTransactionRegistry.GetDatabaseKey(_db),
                committed => CompleteTransaction(changes, committed));
        }
        catch
        {
            _transactionChanges = null;
            await transaction.DisposeAsync();
            throw;
        }
    }

    public async Task<int> SaveChangesAsync(CancellationToken ct = default)
    {
        var pendingChanges = BackendChangeSet.Capture(_db.ChangeTracker);
        var affectedRows = await _db.SaveChangesAsync(ct);
        if (affectedRows == 0 || pendingChanges.IsEmpty)
            return affectedRows;

        if (_transactionChanges is { } transactionChanges)
            transactionChanges.Merge(pendingChanges);
        else
            pendingChanges.Publish(_changePublisher);

        return affectedRows;
    }

    public void ClearTrackedChanges() =>
        _db.ChangeTracker.Clear();

    private void CompleteTransaction(BackendChangeSet changes, bool committed)
    {
        if (ReferenceEquals(_transactionChanges, changes))
            _transactionChanges = null;

        if (committed)
            changes.Publish(_changePublisher);
    }
}
