using PasswordManagerLocal.Common.Backend.Security;
using System.Security.Cryptography;

namespace PasswordManagerLocal.Common.Backend.Models.Encrypted;

public sealed class UserDevicesData : IntegrityCheckableBase, IDisposable
{
    private bool _disposed;

    public List<UserDeviceData> Devices { get; set; } = [];
    public List<DeletedUserDeviceData> DeletedDevices { get; set; } = [];

    public void Dispose()
    {
        if (_disposed)
            return;

        Devices.ForEach(device => device.Dispose());
        DeletedDevices.ForEach(deleted => deleted.Dispose());
        Devices.Clear();
        DeletedDevices.Clear();
        CryptographicOperations.ZeroMemory(IntegrityHash);
        _disposed = true;
        GC.SuppressFinalize(this);
    }

    public override byte[] CalculateIntegrityHash() =>
        Hashing.SHA256Hash(hash =>
        {
            hash.Write(Devices.Count);
            foreach (var device in Devices.OrderBy(device => device.Id))
                hash.WriteBytes(device.IntegrityHash);
            hash.Write(DeletedDevices.Count);
            foreach (var deleted in DeletedDevices.OrderBy(deleted => deleted.Id))
                hash.WriteBytes(deleted.IntegrityHash);
        });

}
