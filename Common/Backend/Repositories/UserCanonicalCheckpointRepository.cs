using Microsoft.EntityFrameworkCore;
using PasswordManagerLocal.Common.Backend.Abstractions.Repositories;
using PasswordManagerLocal.Common.Backend.Models;
using PasswordManagerLocal.Common.Backend.Persistence;

namespace PasswordManagerLocal.Common.Backend.Repositories;

public sealed class UserCanonicalCheckpointRepository : IUserCanonicalCheckpointRepository
{
    private readonly DbSet<UserCanonicalCheckpoint> _checkpoints;

    public UserCanonicalCheckpointRepository(AppDbContext context) => _checkpoints = context.UserCanonicalCheckpoints;

    public Task<UserCanonicalCheckpoint?> GetAsync(Guid userId, CancellationToken ct = default)
    {
        // UpdateCheckpointAsync may stage the first installation-local checkpoint and then invoke
        // synchronization lifecycle validation before the surrounding unit of work is committed.
        // EF database queries do not include Added entities, so consult the tracked local view first.
        // This preserves the atomic user/checkpoint/queue commit while allowing the health gate to
        // verify the checkpoint that is already staged in the current DbContext.
        var tracked = _checkpoints.Local.FirstOrDefault(checkpoint => checkpoint.UserId == userId);
        return tracked is not null
            ? Task.FromResult<UserCanonicalCheckpoint?>(tracked)
            : _checkpoints.FirstOrDefaultAsync(checkpoint => checkpoint.UserId == userId, ct);
    }

    public Task AddAsync(UserCanonicalCheckpoint checkpoint, CancellationToken ct = default) =>
        _checkpoints.AddAsync(checkpoint, ct).AsTask();

    public void Update(UserCanonicalCheckpoint checkpoint) => _checkpoints.Update(checkpoint);
    public void Delete(UserCanonicalCheckpoint checkpoint) => _checkpoints.Remove(checkpoint);
}
