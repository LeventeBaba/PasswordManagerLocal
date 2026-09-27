using PasswordManagerLocal.Common.Backend.Models;

namespace PasswordManagerLocal.Common.Backend.Abstractions.Repositories;

public interface IUserRevisionKnowledgeRepository
{
    Task<UserRevisionKnowledge?> GetAsync(Guid userId, Guid originDeviceId, Guid originInstanceId, long userKeyEpoch, CancellationToken ct = default);
    Task<IReadOnlyList<UserRevisionKnowledge>> ListAsync(Guid userId, long userKeyEpoch, CancellationToken ct = default);
    Task<IReadOnlyList<UserRevisionKnowledge>> ListForUserAsync(Guid userId, CancellationToken ct = default);
    Task<IReadOnlyList<UserRevisionKnowledge>> ListForOriginAsync(Guid userId, Guid originDeviceId, Guid originInstanceId, CancellationToken ct = default);
    Task AddAsync(UserRevisionKnowledge knowledge, CancellationToken ct = default);
    void Update(UserRevisionKnowledge knowledge);
}
