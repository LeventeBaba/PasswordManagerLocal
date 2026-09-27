using PasswordManagerLocal.Common.Backend.Models;

namespace PasswordManagerLocal.Common.Backend.Abstractions.Repositories;

public interface IUserMembershipAuthorizationRepository
{
    Task<UserMembershipAuthorization?> GetByIdAsync(Guid authorizationId, CancellationToken ct = default);
    Task<UserMembershipAuthorization?> GetForSignedEpochAsync(Guid userId, Guid deviceId, Guid originInstanceId, long membershipEpoch, CancellationToken ct = default);
    Task<UserMembershipAuthorization?> GetActiveAsync(Guid userId, Guid deviceId, Guid originInstanceId, CancellationToken ct = default);
    Task<IReadOnlyList<UserMembershipAuthorization>> ListForUserAsync(Guid userId, CancellationToken ct = default);
    Task<IReadOnlyList<UserMembershipAuthorization>> ListActiveForDeviceAsync(Guid userId, Guid deviceId, CancellationToken ct = default);
    Task<IReadOnlyList<Guid>> ListUserIdsForDeviceAsync(Guid deviceId, CancellationToken ct = default);
    Task<IReadOnlyList<Guid>> ListDeviceIdsForUsersAsync(IReadOnlyCollection<Guid> userIds, CancellationToken ct = default);
    Task<bool> HasHistoricalAuthorizationAsync(Guid userId, Guid deviceId, CancellationToken ct = default);
    Task AddAsync(UserMembershipAuthorization authorization, CancellationToken ct = default);
    void Update(UserMembershipAuthorization authorization);
}
