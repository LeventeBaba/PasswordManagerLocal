using PasswordManagerLocal.Common.Backend.Models;

namespace PasswordManagerLocal.Common.Backend.Abstractions.Repositories;

public interface IDeletedUserBarrierRepository
{
    Task<DeletedUserBarrier?> GetAsync(Guid userId, CancellationToken ct = default);
    Task<bool> ExistsAsync(Guid userId, CancellationToken ct = default);
    Task<IReadOnlyList<DeletedUserBarrier>> ListAllAsync(CancellationToken ct = default);
    Task AddAsync(DeletedUserBarrier barrier, CancellationToken ct = default);
    void Update(DeletedUserBarrier barrier);
}
