using PasswordManagerLocal.Common.Backend.Abstractions.Persistence;

namespace PasswordManagerLocal.Common.Tests.Fakes;

public sealed class FakeUnitOfWork : IUnitOfWork
{
    public int SaveCalls { get; private set; }
    public int TransactionCalls { get; private set; }

    public Task<IUnitOfWorkTransaction> BeginTransactionAsync(CancellationToken ct = default)
    {
        TransactionCalls++;
        return Task.FromResult<IUnitOfWorkTransaction>(new FakeUnitOfWorkTransaction());
    }

    public Task<int> SaveChangesAsync(CancellationToken ct = default)
    {
        SaveCalls++;
        return Task.FromResult(1);
    }


    public void ClearTrackedChanges()
    {
    }

}
