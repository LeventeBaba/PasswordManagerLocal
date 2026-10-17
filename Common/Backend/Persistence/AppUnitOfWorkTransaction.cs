using Microsoft.EntityFrameworkCore.Storage;
using PasswordManagerLocal.Common.Backend.Abstractions.Persistence;

namespace PasswordManagerLocal.Common.Backend.Persistence;

internal sealed class AppUnitOfWorkTransaction : IUnitOfWorkTransaction
{
    private readonly IDbContextTransaction _transaction;
    private readonly Action<bool> _complete;
    private IDisposable? _registryLease;
    private int _completed;

    public AppUnitOfWorkTransaction(
        IDbContextTransaction transaction,
        string databaseKey,
        Action<bool> complete)
    {
        _transaction = transaction;
        _complete = complete ?? throw new ArgumentNullException(nameof(complete));
        _registryLease = AppDatabaseTransactionRegistry.Enter(databaseKey);
    }

    public async Task CommitAsync(CancellationToken ct = default)
    {
        try
        {
            await _transaction.CommitAsync(ct);
            Complete(committed: true);
        }
        catch
        {
            Complete(committed: false);
            throw;
        }
        finally
        {
            ReleaseRegistryLease();
        }
    }

    public async Task RollbackAsync(CancellationToken ct = default)
    {
        try
        {
            await _transaction.RollbackAsync(ct);
        }
        finally
        {
            Complete(committed: false);
            ReleaseRegistryLease();
        }
    }

    public async ValueTask DisposeAsync()
    {
        try
        {
            await _transaction.DisposeAsync();
        }
        finally
        {
            Complete(committed: false);
            ReleaseRegistryLease();
        }
    }

    private void Complete(bool committed)
    {
        if (Interlocked.Exchange(ref _completed, 1) == 0)
            _complete(committed);
    }

    private void ReleaseRegistryLease() =>
        Interlocked.Exchange(ref _registryLease, null)?.Dispose();
}
