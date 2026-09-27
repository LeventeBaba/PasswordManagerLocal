using PasswordManagerLocal.Common.Backend.Abstractions.Repositories;
using PasswordManagerLocal.Common.Backend.Models;

namespace PasswordManagerLocal.Common.Tests.Fakes;

public sealed class FakeUserRevisionKnowledgeRepository : IUserRevisionKnowledgeRepository
{
    private readonly List<UserRevisionKnowledge> _items = [];

    public Task<UserRevisionKnowledge?> GetAsync(Guid userId, Guid originDeviceId, Guid originInstanceId, long userKeyEpoch, CancellationToken ct = default) =>
        Task.FromResult(_items.SingleOrDefault(item =>
            item.UserId == userId &&
            item.OriginDeviceId == originDeviceId &&
            item.OriginInstanceId == originInstanceId &&
            item.UserKeyEpoch == userKeyEpoch));

    public Task<IReadOnlyList<UserRevisionKnowledge>> ListAsync(Guid userId, long userKeyEpoch, CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyList<UserRevisionKnowledge>>(_items
            .Where(item => item.UserId == userId && item.UserKeyEpoch == userKeyEpoch)
            .ToList());


    public Task<IReadOnlyList<UserRevisionKnowledge>> ListForUserAsync(Guid userId, CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyList<UserRevisionKnowledge>>(_items
            .Where(item => item.UserId == userId)
            .ToList());

    public Task<IReadOnlyList<UserRevisionKnowledge>> ListForOriginAsync(Guid userId, Guid originDeviceId, Guid originInstanceId, CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyList<UserRevisionKnowledge>>(_items
            .Where(item => item.UserId == userId && item.OriginDeviceId == originDeviceId && item.OriginInstanceId == originInstanceId)
            .ToList());

    public Task AddAsync(UserRevisionKnowledge knowledge, CancellationToken ct = default)
    {
        _items.Add(knowledge);
        return Task.CompletedTask;
    }

    public void Update(UserRevisionKnowledge knowledge)
    {
    }
}
