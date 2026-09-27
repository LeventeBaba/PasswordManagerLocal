using PasswordManagerLocal.Common.Backend.Models;
using PasswordManagerLocal.Common.Backend.Models.Projections;

namespace PasswordManagerLocal.Common.Backend.Abstractions.Repositories;

public interface IGroupRepository : IGenericRepository<Group>
{
    Task<IReadOnlyList<Group>> ListByIdsAsync(IReadOnlyCollection<Guid> ids, CancellationToken ct = default);
    Task<Group?> GetByIdWithUsersAsync(Guid id, CancellationToken ct = default);
    Task<GroupWithUserIdsData?> GetWithUserIdsAsNoTrackingAsync(Guid id, CancellationToken ct = default);
    Task<IReadOnlyList<GroupWithUserIdsData>> ListByUserWithUserIdsAsNoTrackingAsync(Guid userId, CancellationToken ct = default);
    Task<IReadOnlyList<Guid>> ListUserIdsAsync(Guid groupId, CancellationToken ct = default);
    Task<IReadOnlyList<Guid>> ListIdsByUserAsync(Guid userId, CancellationToken ct = default);
}
