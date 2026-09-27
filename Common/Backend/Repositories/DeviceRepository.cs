using Microsoft.EntityFrameworkCore;
using PasswordManagerLocal.Common.Backend.Abstractions.Repositories;
using PasswordManagerLocal.Common.Backend.Models;
using PasswordManagerLocal.Common.Backend.Persistence;
using PasswordManagerLocal.Common.Backend.Utils;

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

    public async Task<IReadOnlyList<Device>> ListDevicesNeedingSyncAsync(CancellationToken ct = default)
    {
        var devices = await Set.AsNoTracking()
            .Where(d => d.IsTrusted &&
                        !d.IsBlocked &&
                        d.TlsCertFingerprint != string.Empty &&
                        d.ItemsNeedingSync.Any(q => q.ProcessedAt == null))
            .ToListAsync(ct);

        return devices.Where(d => d.PublicKey.Length != 0 && d.SignPublicKey.Length != 0).ToList();
    }

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

    public async Task<Device?> GetBySignPublicKeyAsync(byte[] signPublicKey, CancellationToken ct = default)
    {
        if (signPublicKey.Length == 0)
            return null;

        var hash = Security.Hashing.SHA256Hash(signPublicKey);
        var candidates = await Set.Where(d => d.SignPublicKeyHash == hash).ToListAsync(ct);
        return candidates.FirstOrDefault(d => Security.Hashing.Verify(d.SignPublicKey, signPublicKey));
    }

    public Task<Device?> GetByTlsCertFingerprintAsync(string tlsCertFingerprint, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(tlsCertFingerprint))
            return Task.FromResult<Device?>(null);

        var normalized = FingerprintUtil.Normalize(tlsCertFingerprint);
        return Set.FirstOrDefaultAsync(d => d.TlsCertFingerprint == normalized, ct);
    }

    public Task<Device?> GetByTlsCertFingerprintWithUserDevicesAsync(string tlsCertFingerprint, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(tlsCertFingerprint))
            return Task.FromResult<Device?>(null);

        var normalized = FingerprintUtil.Normalize(tlsCertFingerprint);
        return Set
            .Include(d => d.UserDevices)
            .FirstOrDefaultAsync(d => d.TlsCertFingerprint == normalized, ct);
    }

    public async Task<IReadOnlyList<Device>> ListLocalSelfDevicesAsync(
        Guid localDeviceId,
        byte[] signPublicKey,
        string tlsCertFingerprint,
        CancellationToken ct = default)
    {
        var normalizedFingerprint = FingerprintUtil.Normalize(tlsCertFingerprint);
        var signPublicKeyHash = signPublicKey.Length == 0 ? [] : Security.Hashing.SHA256Hash(signPublicKey);
        var candidates = await Set.Include(d => d.UserDevices)
            .Where(d => d.Id == localDeviceId ||
                        (signPublicKeyHash.Length != 0 && d.SignPublicKeyHash == signPublicKeyHash) ||
                        (normalizedFingerprint.Length != 0 && d.TlsCertFingerprint == normalizedFingerprint))
            .ToListAsync(ct);

        return candidates.Where(d =>
                d.Id == localDeviceId ||
                (signPublicKey.Length != 0 && Security.Hashing.Verify(d.SignPublicKey, signPublicKey)) ||
                (normalizedFingerprint.Length != 0 && d.TlsCertFingerprint == normalizedFingerprint))
            .ToList();
    }

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
