using PasswordManagerLocal.Common.Backend.Security;
using System.Security.Cryptography;
using PasswordManagerLocal.Common.Backend.Utils;

namespace PasswordManagerLocal.Common.Backend.Models.Encrypted;

public sealed class CustomUserColor : IntegrityCheckableBase, IDisposable
{
    private bool _disposed;

    public Guid Id { get; set; } = Guid.NewGuid();
    public string? ColorName { get; set; } = null;
    public string ColorCode { get; set; } = string.Empty;
    public DateTime LastUpdatedAt { get; set; } = DateTime.UtcNow;
    public SyncVersionStamp Version { get; set; } = new();

    public void Dispose()
    {
        if (_disposed)
            return;

        Id = Guid.Empty;
        ColorName = null;
        ColorCode = string.Empty;
        LastUpdatedAt = UtcDateTimeUtil.MinDateTime;
        Version = new();
        CryptographicOperations.ZeroMemory(IntegrityHash);
        _disposed = true;
        GC.SuppressFinalize(this);
    }

    public override byte[] CalculateIntegrityHash() =>
        Hashing.SHA256Hash(hash =>
        {
            hash.Write(Id);
            hash.WriteString(ColorName ?? string.Empty);
            hash.WriteString(ColorCode);
            hash.Write(LastUpdatedAt);
            Version.WriteTo(hash);
        });
}
