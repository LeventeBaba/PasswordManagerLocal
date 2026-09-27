using PasswordManagerLocal.Common.Backend.Security;

namespace PasswordManagerLocal.Common.Backend.Models.Encrypted;

public sealed class SecureUserDevices : IntegrityCheckableBase, IDisposable
{
    private bool _disposed;

    public List<UserDeviceData> Devices { get; set; } = [];

    public void Dispose()
    {
        if (_disposed)
            return;

        Devices.ForEach(device => device.Dispose());
        Devices.Clear();
        System.Security.Cryptography.CryptographicOperations.ZeroMemory(IntegrityHash);
        _disposed = true;
        GC.SuppressFinalize(this);
    }

    public override byte[] CalculateIntegrityHash() =>
        Hashing.SHA256Hash(hash =>
        {
            hash.Write(Devices.Count);
            foreach (var device in Devices)
                hash.WriteBytes(device.IntegrityHash);
        });

}
