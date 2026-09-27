namespace PasswordManagerLocal.Common.Backend.Abstractions.Repositories;

public interface ISyncRouteRepository
{
    Task<bool> IsEligibleAsync(Guid userId, Guid remoteDeviceId, CancellationToken ct = default);
    Task<bool> HasAnyEligibleAsync(IReadOnlyCollection<Guid> userIds, Guid remoteDeviceId, CancellationToken ct = default);
    Task<IReadOnlyList<Guid>> ListEligibleUserIdsAsync(IReadOnlyCollection<Guid> userIds, Guid remoteDeviceId, CancellationToken ct = default);
    Task<IReadOnlyList<Guid>> ListAllEligibleUserIdsAsync(Guid remoteDeviceId, CancellationToken ct = default);
    Task<bool> HasEligibleUserForDeviceAsync(Guid deviceId, CancellationToken ct = default);
}
