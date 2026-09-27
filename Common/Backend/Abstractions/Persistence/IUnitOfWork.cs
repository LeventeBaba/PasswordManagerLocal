namespace PasswordManagerLocal.Common.Backend.Abstractions.Persistence;

public interface IUnitOfWork
{
    Task<IUnitOfWorkTransaction> BeginTransactionAsync(CancellationToken ct = default);
    Task<int> SaveChangesAsync(CancellationToken ct = default);
    void ClearTrackedChanges();
}
