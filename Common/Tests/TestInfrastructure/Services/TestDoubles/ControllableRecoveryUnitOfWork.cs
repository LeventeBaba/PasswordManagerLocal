using PasswordManagerLocal.Common.Backend.Abstractions.Persistence;

namespace PasswordManagerLocal.Common.Tests.TestInfrastructure.Services.TestDoubles;

internal sealed class ControllableRecoveryUnitOfWork(IUnitOfWork inner) : IUnitOfWork
{
    public Func<CancellationToken, Task>? BeforeCommitAsync { get; set; }
    public async Task<IUnitOfWorkTransaction> BeginTransactionAsync(CancellationToken ct = default) =>
        new Transaction(await inner.BeginTransactionAsync(ct), this);
    public Task<int> SaveChangesAsync(CancellationToken ct = default) => inner.SaveChangesAsync(ct);
    public void ClearTrackedChanges() => inner.ClearTrackedChanges();

    private sealed class Transaction(IUnitOfWorkTransaction inner, ControllableRecoveryUnitOfWork owner) : IUnitOfWorkTransaction
    {
        public async Task CommitAsync(CancellationToken ct = default)
        {
            if (owner.BeforeCommitAsync is { } callback) await callback(ct);
            await inner.CommitAsync(ct);
        }
        public Task RollbackAsync(CancellationToken ct = default) => inner.RollbackAsync(ct);
        public ValueTask DisposeAsync() => inner.DisposeAsync();
    }
}
