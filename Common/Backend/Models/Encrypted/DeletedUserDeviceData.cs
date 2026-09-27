using PasswordManagerLocal.Common.Backend.Security;
using System.Security.Cryptography;
using System.Text.Json.Serialization;

namespace PasswordManagerLocal.Common.Backend.Models.Encrypted;

public sealed class DeletedUserDeviceData : IntegrityCheckableBase, IDisposable
{
    private bool _disposed;

    public Guid Id { get; set; }
    public DateTimeOffset DeletedAt { get; set; } = DateTimeOffset.UtcNow;
    public SyncVersionStamp Version { get; set; } = new();
    [JsonRequired]
    public TombstoneCausalReference CausalReference { get; set; } = new();

    public void Dispose()
    {
        if (_disposed)
            return;

        Id = Guid.Empty;
        DeletedAt = default;
        Version = new();
        CausalReference = new();
        CryptographicOperations.ZeroMemory(IntegrityHash);
        _disposed = true;
        GC.SuppressFinalize(this);
    }

    public override byte[] CalculateIntegrityHash() =>
        Hashing.SHA256Hash(hash =>
        {
            hash.Write(Id);
            hash.Write(DeletedAt);
            Version.WriteTo(hash);
            CausalReference.WriteTo(hash);
        });
}
