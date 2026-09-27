using Microsoft.EntityFrameworkCore.Storage;
using PasswordManagerLocal.Common.Backend.Abstractions.Persistence;

namespace PasswordManagerLocal.Common.Backend.Persistence;

internal sealed class AppUnitOfWorkTransaction : IUnitOfWorkTransaction
{
    private readonly IDbContextTransaction _transaction;
    private IDisposable? _registryLease;

    public AppUnitOfWorkTransaction(IDbContextTransaction transaction, string databaseKey)
    {
        _transaction = transaction;
        _registryLease = AppDatabaseTransactionRegistry.Enter(databaseKey);
    }

    public async Task CommitAsync(CancellationToken ct = default)
    {
        await _transaction.CommitAsync(ct);
        ReleaseRegistryLease();
    }

    public async Task RollbackAsync(CancellationToken ct = default)
    {
        await _transaction.RollbackAsync(ct);
        ReleaseRegistryLease();
    }

    public async ValueTask DisposeAsync()
    {
        try
        {
            await _transaction.DisposeAsync();
        }
        finally
        {
            ReleaseRegistryLease();
        }
    }

    private void ReleaseRegistryLease() =>
        Interlocked.Exchange(ref _registryLease, null)?.Dispose();
}
