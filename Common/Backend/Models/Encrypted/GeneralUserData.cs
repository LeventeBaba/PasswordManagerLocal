using PasswordManagerLocal.Common.Backend.Models;
using PasswordManagerLocal.Common.Backend.Security;
using PasswordManagerLocal.Common.Backend.Utils;
using System.Security.Cryptography;

namespace PasswordManagerLocal.Common.Backend.Models.Encrypted;

public sealed class GeneralUserData : IntegrityCheckableBase, IDisposable
{
    private bool _disposed;

    public string Username { get; set; } = string.Empty;
    public string FirstName { get; set; } = string.Empty;
    public string LastName { get; set; } = string.Empty;
    public string Email { get; set; } = string.Empty;
    public DateTime RegistrationDate { get; set; } = DateTime.UtcNow;
    public string RegistrationTimeZoneId { get; set; } = string.Empty;
    public DeviceType RegistrationDeviceType { get; set; } = DeviceType.Unknown;
    public DateTime LastUpdatedAt { get; set; } = DateTime.UtcNow;
    public SyncVersionStamp Version { get; set; } = new();

    public void Dispose()
    {
        if (_disposed)
            return;

        Username = string.Empty;
        FirstName = string.Empty;
        LastName = string.Empty;
        Email = string.Empty;
        RegistrationDate = UtcDateTimeUtil.MinDateTime;
        RegistrationTimeZoneId = string.Empty;
        RegistrationDeviceType = DeviceType.Unknown;
        LastUpdatedAt = UtcDateTimeUtil.MinDateTime;
        Version = new();
        CryptographicOperations.ZeroMemory(IntegrityHash);
        _disposed = true;
        GC.SuppressFinalize(this);
    }

    public override byte[] CalculateIntegrityHash() =>
        Hashing.SHA256Hash(hash =>
        {
            hash.WriteString(Username);
            hash.WriteString(FirstName);
            hash.WriteString(LastName);
            hash.WriteString(Email);
            hash.Write(RegistrationDate);
            hash.WriteString(RegistrationTimeZoneId);
            hash.Write((byte)RegistrationDeviceType);
            hash.Write(LastUpdatedAt);
            Version.WriteTo(hash);
        });

}
