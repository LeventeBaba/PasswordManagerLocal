using PasswordManagerLocal.Common.Backend.Models;

namespace PasswordManagerLocal.Common.Backend.Abstractions.Repositories;

public interface IUserSyncFaultRepository
{
    Task<UserSyncFault?> GetActiveAsync(
        Guid userId,
        UserSyncFaultScope scope,
        UserSyncFaultKind kind,
        Guid? originDeviceId,
        Guid? originInstanceId,
        long? keyEpoch,
        CancellationToken ct = default);
    Task<IReadOnlyList<UserSyncFault>> ListForUserAsync(Guid userId, CancellationToken ct = default);
    Task<IReadOnlyList<UserSyncFault>> ListActiveForUserAsync(Guid userId, CancellationToken ct = default);
    Task<IReadOnlyList<UserSyncFault>> ListRecoverableLocalCanonicalAsync(DateTimeOffset nowUtc, CancellationToken ct = default);
    Task<bool> HasBlockingPublishingFaultAsync(Guid userId, CancellationToken ct = default);
    Task<bool> HasTerminalOriginFaultAsync(Guid userId, Guid originDeviceId, Guid originInstanceId, long keyEpoch, CancellationToken ct = default);
    Task AddAsync(UserSyncFault fault, CancellationToken ct = default);
    void Update(UserSyncFault fault);
    void DeleteRange(IEnumerable<UserSyncFault> faults);
}
