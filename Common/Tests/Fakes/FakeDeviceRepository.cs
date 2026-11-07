using PasswordManagerLocal.Common.Backend.Abstractions.Repositories;
using PasswordManagerLocal.Common.Backend.Models;
using PasswordManagerLocal.Common.Backend.Security;
using PasswordManagerLocal.Common.Backend.Utils;

namespace PasswordManagerLocal.Common.Tests.Fakes;

public sealed class FakeDeviceRepository : IDeviceRepository
{
    private readonly Dictionary<Guid, Device> _items = [];

    public IReadOnlyCollection<Device> Items => _items.Values;
    public int UpdateCalls { get; private set; }
    public int DeleteCalls { get; private set; }

    public void Seed(params Device[] devices)
    {
        foreach (var device in devices)
            _items[device.Id] = device;
    }

    public Task<IReadOnlyList<Device>> ListAllAsync(CancellationToken ct = default) =>
        Task.FromResult((IReadOnlyList<Device>)_items.Values.ToList());

    public Task<IReadOnlyList<Device>> ListByIdsAsync(IReadOnlyCollection<Guid> ids, CancellationToken ct = default) =>
        Task.FromResult((IReadOnlyList<Device>)_items.Values.Where(device => ids.Contains(device.Id)).ToList());

    public Task<IReadOnlyList<Device>> ListTrustedUnblockedWithPendingQueueAsync(CancellationToken ct = default) =>
        Task.FromResult((IReadOnlyList<Device>)_items.Values
            .Where(device => device.IsTrusted && !device.IsBlocked && device.TlsCertFingerprint.Length != 0 &&
                device.ItemsNeedingSync.Any(item => item.ProcessedAt is null))
            .ToList());

    public Task<Device?> GetByIdAsync(Guid id, CancellationToken ct = default) =>
        Task.FromResult(_items.GetValueOrDefault(id));

    public Task<Device?> GetByIdAsNoTrackingAsync(Guid id, CancellationToken ct = default) =>
        GetByIdAsync(id, ct);

    public Task<Device?> GetByIdWithUserDevicesAsync(Guid id, CancellationToken ct = default) =>
        GetByIdAsync(id, ct);

    public Task<Device?> GetByIdAsNoTrackingWithUserDevicesAsync(Guid id, CancellationToken ct = default) =>
        GetByIdAsync(id, ct);

    public Task<IReadOnlyList<Guid>> ListActiveUserIdsAsync(Guid deviceId, CancellationToken ct = default) =>
        Task.FromResult((IReadOnlyList<Guid>)(_items.TryGetValue(deviceId, out var device)
            ? device.UserDevices.Where(link => !link.IsDeleted).Select(link => link.UserId).Distinct().ToList()
            : []));

    public Task<IReadOnlyList<Device>> ListBySigningKeyHashAsync(byte[] hash, CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyList<Device>>(_items.Values
            .Where(device => Hashing.SHA256Hash(device.SignPublicKey).SequenceEqual(hash)).ToList());

    public Task<Device?> GetByNormalizedFingerprintAsync(string fingerprint, CancellationToken ct = default) =>
        Task.FromResult(_items.Values.FirstOrDefault(device =>
            FingerprintUtil.Normalize(device.TlsCertFingerprint) == fingerprint));

    public Task<Device?> GetByNormalizedFingerprintWithUserDevicesAsync(string fingerprint, CancellationToken ct = default) =>
        GetByNormalizedFingerprintAsync(fingerprint, ct);

    public Task<IReadOnlyList<Device>> ListLocalIdentityCandidatesAsync(
        Guid localDeviceId,
        byte[] signingKeyHash,
        string fingerprint,
        CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyList<Device>>(_items.Values.Where(device =>
            device.Id == localDeviceId ||
            (signingKeyHash.Length != 0 && Hashing.SHA256Hash(device.SignPublicKey).SequenceEqual(signingKeyHash)) ||
            (fingerprint.Length != 0 && FingerprintUtil.Normalize(device.TlsCertFingerprint) == fingerprint)).ToList());

    public Task<IReadOnlyList<Device>> ListByIdsWithUserDevicesAsNoTrackingAsync(
        IReadOnlyCollection<Guid> deviceIds,
        Guid excludedDeviceId,
        CancellationToken ct = default) =>
        Task.FromResult((IReadOnlyList<Device>)_items.Values
            .Where(device => device.Id != excludedDeviceId && deviceIds.Contains(device.Id))
            .ToList());

    public Task<IReadOnlyList<Device>> ListTrustedUnblockedAsync(CancellationToken ct = default) =>
        Task.FromResult((IReadOnlyList<Device>)_items.Values
            .Where(device => device.IsTrusted && !device.IsBlocked)
            .ToList());

    public Task<(bool found, Device? entity)> TryGetByIdAsync(Guid id, CancellationToken ct = default)
    {
        var found = _items.TryGetValue(id, out var device);
        return Task.FromResult((found, device));
    }

    public Task AddAsync(Device entity, CancellationToken ct = default)
    {
        _items[entity.Id] = entity;
        return Task.CompletedTask;
    }

    public void Update(Device entity)
    {
        UpdateCalls++;
        _items[entity.Id] = entity;
    }

    public void Delete(Device entity)
    {
        DeleteCalls++;
        _items.Remove(entity.Id);
    }

    public Task<bool> ExistsAsync(Guid id, CancellationToken ct = default) =>
        Task.FromResult(_items.ContainsKey(id));
}
