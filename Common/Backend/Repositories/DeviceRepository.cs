using Microsoft.EntityFrameworkCore;
using PasswordManagerLocal.Common.Backend.Abstractions.Repositories;
using PasswordManagerLocal.Common.Backend.Models;
using PasswordManagerLocal.Common.Backend.Persistence;

namespace PasswordManagerLocal.Common.Backend.Repositories;

public sealed class DeviceRepository : GenericRepositoryBase<Device>, IDeviceRepository
{
    public DeviceRepository(AppDbContext context) : base(context.Devices) { }

    public override Task<bool> ExistsAsync(Guid id, CancellationToken ct = default) =>
        Set.AsNoTracking().AnyAsync(d => d.Id == id, ct);

    public override Task<Device?> GetByIdAsync(Guid id, CancellationToken ct = default) =>
        Set.FirstOrDefaultAsync(d => d.Id == id, ct);

    public async Task<IReadOnlyList<Device>> ListByIdsAsync(IReadOnlyCollection<Guid> ids, CancellationToken ct = default)
    {
        if (ids.Count == 0)
            return [];

        return await Set.Where(d => ids.Contains(d.Id)).ToListAsync(ct);
    }

    public async Task<IReadOnlyList<Device>> ListTrustedUnblockedWithPendingQueueAsync(CancellationToken ct = default) =>
        await Set.AsNoTracking()
            .Where(d => d.IsTrusted &&
                        !d.IsBlocked &&
                        d.TlsCertFingerprint != string.Empty &&
                        d.ItemsNeedingSync.Any(q => q.ProcessedAt == null))
            .ToListAsync(ct);

    public Task<Device?> GetByIdAsNoTrackingAsync(Guid id, CancellationToken ct = default) =>
        Set.AsNoTracking().FirstOrDefaultAsync(d => d.Id == id, ct);

    public Task<Device?> GetByIdWithUserDevicesAsync(Guid id, CancellationToken ct = default) =>
        Set.Include(device => device.UserDevices)
            .FirstOrDefaultAsync(device => device.Id == id, ct);

    public Task<Device?> GetByIdAsNoTrackingWithUserDevicesAsync(Guid id, CancellationToken ct = default) =>
        Set.AsNoTracking()
            .Include(device => device.UserDevices)
            .FirstOrDefaultAsync(device => device.Id == id, ct);

    public async Task<IReadOnlyList<Guid>> ListActiveUserIdsAsync(Guid deviceId, CancellationToken ct = default) =>
        await Set.AsNoTracking()
            .Where(device => device.Id == deviceId)
            .SelectMany(device => device.UserDevices
                .Where(link => !link.IsDeleted)
                .Select(link => link.UserId))
            .Distinct()
            .ToListAsync(ct);

    public async Task<IReadOnlyList<Device>> ListBySigningKeyHashAsync(byte[] hash, CancellationToken ct = default) =>
        await Set.Where(d => d.SignPublicKeyHash == hash).ToListAsync(ct);

    public Task<Device?> GetByNormalizedFingerprintAsync(string fingerprint, CancellationToken ct = default) =>
        Set.FirstOrDefaultAsync(d => d.TlsCertFingerprint == fingerprint, ct);

    public Task<Device?> GetByNormalizedFingerprintWithUserDevicesAsync(string fingerprint, CancellationToken ct = default) =>
        Set.Include(d => d.UserDevices).FirstOrDefaultAsync(d => d.TlsCertFingerprint == fingerprint, ct);

    public async Task<IReadOnlyList<Device>> ListLocalIdentityCandidatesAsync(
        Guid localDeviceId,
        byte[] signingKeyHash,
        string fingerprint,
        CancellationToken ct = default) =>
        await Set.Include(d => d.UserDevices)
            .Where(d => d.Id == localDeviceId ||
                        (signingKeyHash.Length != 0 && d.SignPublicKeyHash == signingKeyHash) ||
                        (fingerprint.Length != 0 && d.TlsCertFingerprint == fingerprint))
            .ToListAsync(ct);

    public async Task<IReadOnlyList<Device>> ListByIdsWithUserDevicesAsNoTrackingAsync(
        IReadOnlyCollection<Guid> deviceIds,
        Guid excludedDeviceId,
        CancellationToken ct = default)
    {
        if (deviceIds.Count == 0)
            return [];

        return await Set.AsNoTracking()
            .Include(d => d.UserDevices)
            .Where(d => d.Id != excludedDeviceId && deviceIds.Contains(d.Id))
            .ToListAsync(ct);
    }

    public async Task<IReadOnlyList<Device>> ListTrustedUnblockedAsync(CancellationToken ct = default) =>
        await Set.AsNoTracking()
            .Where(d => d.IsTrusted && !d.IsBlocked)
            .ToListAsync(ct);

}
