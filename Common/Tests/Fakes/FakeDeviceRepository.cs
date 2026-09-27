using PasswordManagerLocal.Common.Backend.Abstractions.Repositories;
using PasswordManagerLocal.Common.Backend.Models;

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

    public Task<IReadOnlyList<Device>> ListDevicesNeedingSyncAsync(CancellationToken ct = default) =>
        Task.FromResult((IReadOnlyList<Device>)_items.Values
            .Where(device => device.ItemsNeedingSync.Any(item => item.ProcessedAt is null))
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

    public Task<Device?> GetBySignPublicKeyAsync(byte[] signPublicKey, CancellationToken ct = default) =>
        Task.FromResult(_items.Values.FirstOrDefault(device => device.SignPublicKey.SequenceEqual(signPublicKey)));

    public Task<Device?> GetByTlsCertFingerprintAsync(string tlsCertFingerprint, CancellationToken ct = default) =>
        Task.FromResult(_items.Values.FirstOrDefault(device =>
            string.Equals(device.TlsCertFingerprint, tlsCertFingerprint, StringComparison.OrdinalIgnoreCase)));

    public Task<Device?> GetByTlsCertFingerprintWithUserDevicesAsync(string tlsCertFingerprint, CancellationToken ct = default) =>
        GetByTlsCertFingerprintAsync(tlsCertFingerprint, ct);

    public Task<IReadOnlyList<Device>> ListLocalSelfDevicesAsync(
        Guid localDeviceId,
        byte[] signPublicKey,
        string tlsCertFingerprint,
        CancellationToken ct = default) =>
        Task.FromResult((IReadOnlyList<Device>)_items.Values.Where(device =>
            device.Id == localDeviceId ||
            device.SignPublicKey.SequenceEqual(signPublicKey) ||
            string.Equals(device.TlsCertFingerprint, tlsCertFingerprint, StringComparison.OrdinalIgnoreCase)).ToList());

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
