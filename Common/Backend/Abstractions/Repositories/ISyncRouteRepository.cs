namespace PasswordManagerLocal.Common.Backend.Abstractions.Repositories;

public interface ISyncRouteRepository
{
    Task<IReadOnlyList<Guid>> ListAllControlEligibleUserIdsAsync(Guid remoteDeviceId, CancellationToken ct = default) =>
        ListAllEligibleUserIdsAsync(remoteDeviceId, ct);
    Task<bool> IsControlEligibleAsync(Guid userId, Guid remoteDeviceId, CancellationToken ct = default) =>
        IsEligibleAsync(userId, remoteDeviceId, ct);
    Task<bool> IsEligibleAsync(Guid userId, Guid remoteDeviceId, CancellationToken ct = default);
    Task<bool> HasAnyEligibleAsync(IReadOnlyCollection<Guid> userIds, Guid remoteDeviceId, CancellationToken ct = default);
    Task<IReadOnlyList<Guid>> ListEligibleUserIdsAsync(IReadOnlyCollection<Guid> userIds, Guid remoteDeviceId, CancellationToken ct = default);
    Task<IReadOnlyList<Guid>> ListAllEligibleUserIdsAsync(Guid remoteDeviceId, CancellationToken ct = default);
    Task<bool> HasEligibleUserForDeviceAsync(Guid deviceId, CancellationToken ct = default);
}
