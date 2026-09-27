using Microsoft.EntityFrameworkCore;
using PasswordManagerLocal.Common.Backend.Abstractions.Repositories;
using PasswordManagerLocal.Common.Backend.Models;
using PasswordManagerLocal.Common.Backend.Persistence;

namespace PasswordManagerLocal.Common.Backend.Repositories;

public sealed class DeletedUserBarrierRepository : IDeletedUserBarrierRepository
{
    private readonly DbSet<DeletedUserBarrier> _barriers;

    public DeletedUserBarrierRepository(AppDbContext context) =>
        _barriers = context.DeletedUserBarriers;

    public Task<DeletedUserBarrier?> GetAsync(Guid userId, CancellationToken ct = default) =>
        _barriers.FirstOrDefaultAsync(barrier => barrier.UserId == userId, ct);

    public Task<bool> ExistsAsync(Guid userId, CancellationToken ct = default) =>
        _barriers.AsNoTracking().AnyAsync(barrier => barrier.UserId == userId, ct);

    public async Task<IReadOnlyList<DeletedUserBarrier>> ListAllAsync(CancellationToken ct = default) =>
        await _barriers.AsNoTracking().OrderBy(barrier => barrier.UserId).ToListAsync(ct);

    public Task AddAsync(DeletedUserBarrier barrier, CancellationToken ct = default) =>
        _barriers.AddAsync(barrier, ct).AsTask();

    public void Update(DeletedUserBarrier barrier) =>
        _barriers.Update(barrier);
}
