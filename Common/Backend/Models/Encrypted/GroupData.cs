using PasswordManagerLocal.Common.Backend.Security;
using System.Security.Cryptography;
using PasswordManagerLocal.Common.Backend.Utils;

namespace PasswordManagerLocal.Common.Backend.Models.Encrypted;

public sealed class GroupData : IntegrityCheckableBase, IDisposable
{
    private bool _disposed;

    public Guid Id { get; set; } = Guid.NewGuid();
    public string Name { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime LastUpdatedAt { get; set; } = DateTime.UtcNow;
    public SecurePasswords Passwords { get; set; } = new();


    public void Dispose()
    {
        Dispose(disposing: true);
        GC.SuppressFinalize(this);
    }

    private void Dispose(bool disposing)
    {
        if (_disposed)
            return;

        Id = Guid.Empty;
        Name = string.Empty;
        Description = string.Empty;
        CreatedAt = UtcDateTimeUtil.MinDateTime;
        LastUpdatedAt = UtcDateTimeUtil.MinDateTime;
        CryptographicOperations.ZeroMemory(IntegrityHash);

        if (disposing)
            Passwords.Dispose();

        _disposed = true;
    }



    public override byte[] CalculateIntegrityHash() =>
        Hashing.SHA256Hash(hash =>
        {
            hash.Write(Id);
            hash.WriteString(Name);
            hash.WriteString(Description);
            hash.Write(CreatedAt);
            hash.Write(LastUpdatedAt);
            hash.WriteBytes(Passwords.IntegrityHash);
        });

}
