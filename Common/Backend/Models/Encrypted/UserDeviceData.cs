using PasswordManagerLocal.Common.Backend.Security;
using PasswordManagerLocal.Common.Backend.Utils;

namespace PasswordManagerLocal.Common.Backend.Models.Encrypted;

public sealed class UserDeviceData : IntegrityCheckableBase, IDisposable
{
    private bool _disposed;

    public Guid Id { get; set; }
    public string Name { get; set; } = string.Empty;
    public DateTimeOffset LinkedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTime LastLoginDate { get; set; } = UtcDateTimeUtil.MinDateTime;
    public DateTime? PreviousLoginDate { get; set; }
    public DateTimeOffset LastUpdatedAt { get; set; } = DateTimeOffset.UtcNow;
    public SyncVersionStamp Version { get; set; } = new();

    public void Dispose()
    {
        if (_disposed)
            return;

        Id = Guid.Empty;
        Name = string.Empty;
        LinkedAt = default;
        LastLoginDate = UtcDateTimeUtil.MinDateTime;
        PreviousLoginDate = null;
        LastUpdatedAt = default;
        Version = new();
        System.Security.Cryptography.CryptographicOperations.ZeroMemory(IntegrityHash);
        _disposed = true;
        GC.SuppressFinalize(this);
    }

    public override byte[] CalculateIntegrityHash() =>
        Hashing.SHA256Hash(hash =>
        {
            hash.Write(Id);
            hash.WriteString(Name);
            hash.Write(LinkedAt);
            hash.Write(LastLoginDate);
            hash.Write(PreviousLoginDate.HasValue);
            if (PreviousLoginDate.HasValue)
                hash.Write(PreviousLoginDate.Value);
            hash.Write(LastUpdatedAt);
            Version.WriteTo(hash);
        });
}
