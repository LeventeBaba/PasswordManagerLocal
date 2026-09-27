using PasswordManagerLocal.Common.Backend.Security;
using PasswordManagerLocal.Common.Backend.Constants;
using System.Security.Cryptography;
using System.Text.Json.Serialization;

namespace PasswordManagerLocal.Common.Backend.Models.Encrypted;

public sealed class UserData : IntegrityCheckableBase, IDisposable
{
    private bool _disposed;

    [JsonRequired]
    public int FormatVersion { get; set; } = SyncConstants.EncryptedUserDataFormatVersion;
    public Guid UId { get; set; } = Guid.NewGuid();
    public byte[] GeneralUserDataKey { get; set; } = [];
    public byte[] GeneralUserDataIntegrityHash { get; set; } = [];
    public byte[] UserPasswordsDataKey { get; set; } = [];
    public byte[] UserPasswordsDataIntegrityHash { get; set; } = [];
    public byte[] UserDevicesDataKey { get; set; } = [];
    public byte[] UserDevicesDataIntegrityHash { get; set; } = [];

    public void Dispose()
    {
        Dispose(disposing: true);
        GC.SuppressFinalize(this);
    }

    private void Dispose(bool disposing)
    {
        if (_disposed)
            return;

        FormatVersion = 0;
        UId = Guid.Empty;
        CryptographicOperations.ZeroMemory(GeneralUserDataKey);
        CryptographicOperations.ZeroMemory(GeneralUserDataIntegrityHash);
        CryptographicOperations.ZeroMemory(UserPasswordsDataKey);
        CryptographicOperations.ZeroMemory(UserPasswordsDataIntegrityHash);
        CryptographicOperations.ZeroMemory(UserDevicesDataKey);
        CryptographicOperations.ZeroMemory(UserDevicesDataIntegrityHash);
        CryptographicOperations.ZeroMemory(IntegrityHash);

        _disposed = true;
    }

    public override byte[] CalculateIntegrityHash() =>
        Hashing.SHA256Hash(hash =>
        {
            hash.Write(FormatVersion);
            hash.Write(UId);
            hash.WriteBytes(GeneralUserDataKey);
            hash.WriteBytes(GeneralUserDataIntegrityHash);
            hash.WriteBytes(UserPasswordsDataKey);
            hash.WriteBytes(UserPasswordsDataIntegrityHash);
            hash.WriteBytes(UserDevicesDataKey);
            hash.WriteBytes(UserDevicesDataIntegrityHash);
        });

}
