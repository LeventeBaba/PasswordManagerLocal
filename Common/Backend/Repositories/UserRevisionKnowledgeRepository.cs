using Microsoft.EntityFrameworkCore;
using PasswordManagerLocal.Common.Backend.Abstractions.Repositories;
using PasswordManagerLocal.Common.Backend.Models;
using PasswordManagerLocal.Common.Backend.Persistence;

namespace PasswordManagerLocal.Common.Backend.Repositories;

public sealed class UserRevisionKnowledgeRepository : IUserRevisionKnowledgeRepository
{
    private readonly DbSet<UserRevisionKnowledge> _knowledge;

    public UserRevisionKnowledgeRepository(AppDbContext context)
    {
        _knowledge = context.UserRevisionKnowledge;
    }

    public Task<UserRevisionKnowledge?> GetAsync(Guid userId, Guid originDeviceId, Guid originInstanceId, long userKeyEpoch, CancellationToken ct = default) =>
        _knowledge.FirstOrDefaultAsync(item =>
            item.UserId == userId &&
            item.OriginDeviceId == originDeviceId &&
            item.OriginInstanceId == originInstanceId &&
            item.UserKeyEpoch == userKeyEpoch, ct);

    public async Task<IReadOnlyList<UserRevisionKnowledge>> ListAsync(Guid userId, long userKeyEpoch, CancellationToken ct = default) =>
        await _knowledge
            .Where(item => item.UserId == userId && item.UserKeyEpoch == userKeyEpoch)
            .OrderBy(item => item.OriginDeviceId)
            .ThenBy(item => item.OriginInstanceId)
            .ToListAsync(ct);

    public async Task<IReadOnlyList<UserRevisionKnowledge>> ListForUserAsync(Guid userId, CancellationToken ct = default) =>
        await _knowledge.Where(item => item.UserId == userId)
            .OrderBy(item => item.OriginDeviceId).ThenBy(item => item.OriginInstanceId).ThenBy(item => item.UserKeyEpoch)
            .ToListAsync(ct);

    public async Task<IReadOnlyList<UserRevisionKnowledge>> ListForOriginAsync(Guid userId, Guid originDeviceId, Guid originInstanceId, CancellationToken ct = default) =>
        await _knowledge.Where(item => item.UserId == userId && item.OriginDeviceId == originDeviceId && item.OriginInstanceId == originInstanceId)
            .OrderBy(item => item.UserKeyEpoch).ToListAsync(ct);

    public Task AddAsync(UserRevisionKnowledge knowledge, CancellationToken ct = default) =>
        _knowledge.AddAsync(knowledge, ct).AsTask();

    public void Update(UserRevisionKnowledge knowledge) => _knowledge.Update(knowledge);
}
