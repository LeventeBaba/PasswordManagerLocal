using PasswordManagerLocal.Common.Backend.Models;

namespace PasswordManagerLocal.Common.Backend.Abstractions.Services;

public interface ISyncDeviceIdentityService
{
    bool TryAdd(Device device);
    int TryAdd(IReadOnlyList<Device> devices);
    bool TryRemove(Device device);
    int Count();
    bool Exists(Device device);
    bool ContainsFingerprint(string fingerprint);
    bool TryGetByFingerprint(string fingerprint, out Device? device);
    bool ContainsId(Guid deviceId);
    bool TryGetById(Guid deviceId, out Device? device);
    bool IsEmpty();
    void Clear();
}