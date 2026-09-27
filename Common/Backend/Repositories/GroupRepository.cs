using Microsoft.EntityFrameworkCore;
using PasswordManagerLocal.Common.Backend.Abstractions.Repositories;
using PasswordManagerLocal.Common.Backend.Models;
using PasswordManagerLocal.Common.Backend.Models.Projections;
using PasswordManagerLocal.Common.Backend.Persistence;

namespace PasswordManagerLocal.Common.Backend.Repositories;

public sealed class GroupRepository : GenericRepositoryBase<Group>, IGroupRepository
{
    public GroupRepository(AppDbContext context) : base(context.Groups) { }

    public override Task<bool> ExistsAsync(Guid id, CancellationToken ct = default) =>
        Set.AsNoTracking().AnyAsync(group => group.Id == id, ct);

    public override Task<Group?> GetByIdAsync(Guid id, CancellationToken ct = default) =>
        Set.FirstOrDefaultAsync(group => group.Id == id, ct);

    public async Task<IReadOnlyList<Group>> ListByIdsAsync(IReadOnlyCollection<Guid> ids, CancellationToken ct = default)
    {
        if (ids.Count == 0)
            return [];

        return await Set.Where(group => ids.Contains(group.Id)).ToListAsync(ct);
    }

    public Task<Group?> GetByIdWithUsersAsync(Guid id, CancellationToken ct = default) =>
        Set.Include(group => group.Users)
            .FirstOrDefaultAsync(group => group.Id == id, ct);

    public Task<GroupWithUserIdsData?> GetWithUserIdsAsNoTrackingAsync(Guid id, CancellationToken ct = default) =>
        Set.AsNoTracking()
            .Where(group => group.Id == id)
            .Select(group => new GroupWithUserIdsData
            {
                Id = group.Id,
                EncryptedPayload = group.EncryptedPayload,
                LastModifiedAt = group.LastModifiedAt,
                IntegrityHash = group.IntegrityHash,
                UserIds = group.Users.Select(user => user.UId).ToList()
            })
            .FirstOrDefaultAsync(ct);

    public async Task<IReadOnlyList<GroupWithUserIdsData>> ListByUserWithUserIdsAsNoTrackingAsync(
        Guid userId,
        CancellationToken ct = default) =>
        await Set.AsNoTracking()
            .Where(group => group.Users.Any(user => user.UId == userId))
            .Select(group => new GroupWithUserIdsData
            {
                Id = group.Id,
                EncryptedPayload = group.EncryptedPayload,
                LastModifiedAt = group.LastModifiedAt,
                IntegrityHash = group.IntegrityHash,
                UserIds = group.Users.Select(user => user.UId).ToList()
            })
            .ToListAsync(ct);

    public async Task<IReadOnlyList<Guid>> ListUserIdsAsync(Guid groupId, CancellationToken ct = default) =>
        await Set.AsNoTracking()
            .Where(group => group.Id == groupId)
            .SelectMany(group => group.Users.Select(user => user.UId))
            .Distinct()
            .ToListAsync(ct);

    public async Task<IReadOnlyList<Guid>> ListIdsByUserAsync(Guid userId, CancellationToken ct = default) =>
        await Set.AsNoTracking()
            .Where(group => group.Users.Any(user => user.UId == userId))
            .Select(group => group.Id)
            .ToListAsync(ct);
}
