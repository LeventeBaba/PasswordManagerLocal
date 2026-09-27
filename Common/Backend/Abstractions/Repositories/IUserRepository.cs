using PasswordManagerLocal.Common.Backend.Models;

namespace PasswordManagerLocal.Common.Backend.Abstractions.Repositories;

public interface IUserRepository : IGenericRepository<User>
{
    Task<IReadOnlyList<Guid>> ListUserIdsAsync(CancellationToken ct = default);
    Task<IReadOnlyList<UserLoginLookupData>> ListLoginLookupDataAsync(CancellationToken ct = default);
    Task<IReadOnlyList<UserLoginIdentityState>> ListLoginIdentityStatesAsync(CancellationToken ct = default);
    Task<UserLoginIdentityState?> GetLoginIdentityStateAsync(Guid userId, CancellationToken ct = default);
    Task AddLoginIdentityStateAsync(UserLoginIdentityState state, CancellationToken ct = default);
    void UpdateLoginIdentityState(UserLoginIdentityState state);
    void DeleteLoginIdentityState(UserLoginIdentityState state);
    Task<IReadOnlyList<User>> GetAllRememberMeEnabledUsersAsync(CancellationToken ct = default);
    Task<User?> GetByIdAsNoTrackingAsync(Guid id, CancellationToken ct = default);
    Task<IReadOnlyList<User>> ListByIdsAsync(IReadOnlyCollection<Guid> ids, CancellationToken ct = default);
    Task<User?> GetByIdWithRelationsAsync(Guid id, CancellationToken ct = default);
    Task<User?> GetByIdAsNoTrackingWithRelationsAsync(Guid id, CancellationToken ct = default);
    Task UpdateSavedKeyAsync(Guid id, byte[]? savedKey, CancellationToken ct = default);
}