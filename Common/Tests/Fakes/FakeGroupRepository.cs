using PasswordManagerLocal.Common.Backend.Abstractions.Repositories;
using PasswordManagerLocal.Common.Backend.Models;
using PasswordManagerLocal.Common.Backend.Models.Projections;

namespace PasswordManagerLocal.Common.Tests.Fakes;

public sealed class FakeGroupRepository : IGroupRepository
{
    private readonly Dictionary<Guid, Group> _items = [];

    public void Seed(params Group[] groups)
    {
        foreach (var group in groups)
            _items[group.Id] = group;
    }

    public Task<IReadOnlyList<Group>> ListAllAsync(CancellationToken ct = default) =>
        Task.FromResult((IReadOnlyList<Group>)_items.Values.ToList());

    public Task<IReadOnlyList<Group>> ListByIdsAsync(IReadOnlyCollection<Guid> ids, CancellationToken ct = default) =>
        Task.FromResult((IReadOnlyList<Group>)_items.Values.Where(group => ids.Contains(group.Id)).ToList());

    public Task<Group?> GetByIdAsync(Guid id, CancellationToken ct = default) =>
        Task.FromResult(_items.GetValueOrDefault(id));

    public Task<Group?> GetByIdWithUsersAsync(Guid id, CancellationToken ct = default) =>
        GetByIdAsync(id, ct);

    public Task<GroupWithUserIdsData?> GetWithUserIdsAsNoTrackingAsync(Guid id, CancellationToken ct = default) =>
        Task.FromResult(_items.TryGetValue(id, out var group) ? Project(group) : null);

    public Task<IReadOnlyList<GroupWithUserIdsData>> ListByUserWithUserIdsAsNoTrackingAsync(
        Guid userId,
        CancellationToken ct = default) =>
        Task.FromResult((IReadOnlyList<GroupWithUserIdsData>)_items.Values
            .Where(group => group.Users.Any(user => user.UId == userId))
            .Select(Project)
            .ToList());

    public Task<IReadOnlyList<Guid>> ListUserIdsAsync(Guid groupId, CancellationToken ct = default) =>
        Task.FromResult((IReadOnlyList<Guid>)(_items.TryGetValue(groupId, out var group)
            ? group.Users.Select(user => user.UId).ToList()
            : []));

    public Task<IReadOnlyList<Guid>> ListIdsByUserAsync(Guid userId, CancellationToken ct = default) =>
        Task.FromResult((IReadOnlyList<Guid>)_items.Values
            .Where(group => group.Users.Any(user => user.UId == userId))
            .Select(group => group.Id)
            .ToList());

    public Task<(bool found, Group? entity)> TryGetByIdAsync(Guid id, CancellationToken ct = default)
    {
        var found = _items.TryGetValue(id, out var group);
        return Task.FromResult((found, group));
    }

    public Task AddAsync(Group entity, CancellationToken ct = default)
    {
        _items[entity.Id] = entity;
        return Task.CompletedTask;
    }

    public void Update(Group entity) =>
        _items[entity.Id] = entity;

    public void Delete(Group entity) =>
        _items.Remove(entity.Id);

    public Task<bool> ExistsAsync(Guid id, CancellationToken ct = default) =>
        Task.FromResult(_items.ContainsKey(id));

    private static GroupWithUserIdsData Project(Group group) =>
        new()
        {
            Id = group.Id,
            EncryptedPayload = group.EncryptedPayload,
            LastModifiedAt = group.LastModifiedAt,
            IntegrityHash = group.IntegrityHash,
            UserIds = group.Users.Select(user => user.UId).ToList()
        };
}
