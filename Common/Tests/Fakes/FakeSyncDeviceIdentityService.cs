using PasswordManagerLocal.Common.Backend.Abstractions.Services;
using PasswordManagerLocal.Common.Backend.Models;

namespace PasswordManagerLocal.Common.Tests.Fakes;

public sealed class FakeSyncDeviceIdentityService : ISyncDeviceIdentityService
{
    private readonly Dictionary<Guid, Device> _items = [];

    public int AddCalls { get; private set; }
    public int RemoveCalls { get; private set; }
    public int ClearCalls { get; private set; }

    public bool TryAdd(Device device)
    {
        AddCalls++;
        _items[device.Id] = device;
        return true;
    }

    public int TryAdd(IReadOnlyList<Device> devices)
    {
        foreach (var device in devices)
            TryAdd(device);
        return devices.Count;
    }

    public bool TryRemove(Device device)
    {
        RemoveCalls++;
        return _items.Remove(device.Id);
    }

    public int Count() => _items.Count;
    public bool Exists(Device device) => _items.ContainsKey(device.Id);
    public bool ContainsFingerprint(string fingerprint) => _items.Values.Any(device => string.Equals(device.TlsCertFingerprint, fingerprint, StringComparison.OrdinalIgnoreCase));

    public bool TryGetByFingerprint(string fingerprint, out Device? device)
    {
        device = _items.Values.FirstOrDefault(item => string.Equals(item.TlsCertFingerprint, fingerprint, StringComparison.OrdinalIgnoreCase));
        return device is not null;
    }

    public bool ContainsId(Guid deviceId) => _items.ContainsKey(deviceId);
    public bool TryGetById(Guid deviceId, out Device? device) => _items.TryGetValue(deviceId, out device);
    public bool IsEmpty() => _items.Count == 0;

    public void Clear()
    {
        ClearCalls++;
        _items.Clear();
    }
}
