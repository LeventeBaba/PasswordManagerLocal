using PasswordManagerLocal.Common.Backend.Models;

namespace PasswordManagerLocal.Common.Backend.Abstractions.Repositories;

public interface IUserOriginRemovalCutoffRepository
{
    Task<UserOriginRemovalCutoff?> GetAsync(Guid userId, Guid deviceId, Guid originInstanceId, long userKeyEpoch, CancellationToken ct = default);
    Task<IReadOnlyList<UserOriginRemovalCutoff>> ListForUserAsync(Guid userId, CancellationToken ct = default);
    Task<IReadOnlyList<UserOriginRemovalCutoff>> ListForOriginAsync(Guid userId, Guid deviceId, Guid originInstanceId, CancellationToken ct = default);
    Task AddAsync(UserOriginRemovalCutoff cutoff, CancellationToken ct = default);
}
