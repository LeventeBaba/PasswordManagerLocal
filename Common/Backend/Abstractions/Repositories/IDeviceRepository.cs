using PasswordManagerLocal.Common.Backend.Models;

namespace PasswordManagerLocal.Common.Backend.Abstractions.Repositories;

public interface IDeviceRepository : IGenericRepository<Device>
{
    Task<IReadOnlyList<Device>> ListByIdsAsync(IReadOnlyCollection<Guid> ids, CancellationToken ct = default);
    Task<IReadOnlyList<Device>> ListTrustedUnblockedWithPendingQueueAsync(CancellationToken ct = default);
    Task<Device?> GetByIdAsNoTrackingAsync(Guid id, CancellationToken ct = default);
    Task<Device?> GetByIdWithUserDevicesAsync(Guid id, CancellationToken ct = default);
    Task<Device?> GetByIdAsNoTrackingWithUserDevicesAsync(Guid id, CancellationToken ct = default);
    Task<IReadOnlyList<Guid>> ListActiveUserIdsAsync(Guid deviceId, CancellationToken ct = default);
    Task<IReadOnlyList<Device>> ListBySigningKeyHashAsync(byte[] hash, CancellationToken ct = default);
    Task<Device?> GetByNormalizedFingerprintAsync(string tlsCertFingerprint, CancellationToken ct = default);
    Task<Device?> GetByNormalizedFingerprintWithUserDevicesAsync(string tlsCertFingerprint, CancellationToken ct = default);
    Task<IReadOnlyList<Device>> ListLocalIdentityCandidatesAsync(Guid localDeviceId, byte[] signingKeyHash, string fingerprint, CancellationToken ct = default);
    Task<IReadOnlyList<Device>> ListByIdsWithUserDevicesAsNoTrackingAsync(IReadOnlyCollection<Guid> deviceIds, Guid excludedDeviceId, CancellationToken ct = default);
    Task<IReadOnlyList<Device>> ListTrustedUnblockedAsync(CancellationToken ct = default);
}
