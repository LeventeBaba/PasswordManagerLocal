using PasswordManagerLocal.Common.Backend.Security;
using System.Security.Cryptography;

namespace PasswordManagerLocal.Common.Backend.Models.Encrypted;

public sealed class SecurePasswords : IntegrityCheckableBase, IDisposable
{
    private bool _disposed;

    public List<SecurePassword> Passwords { get; set; } = [];
    public byte[] PasswordKey { get; set; } = [];



    public void Dispose()
    {
        Dispose(disposing: true);
        GC.SuppressFinalize(this);
    }


    private void Dispose(bool disposing)
    {
        if (_disposed)
            return;

        CryptographicOperations.ZeroMemory(PasswordKey);
        if (disposing)
            Passwords.ForEach(pw => pw.Dispose());
        Passwords.Clear();

        _disposed = true;
    }


    public override byte[] CalculateIntegrityHash() =>
        Hashing.SHA256Hash(hash =>
        {
            hash.WriteBytes(PasswordKey);
            hash.Write(Passwords.Count);
            foreach (var password in Passwords)
                hash.WriteBytes(password.IntegrityHash);
        });

}