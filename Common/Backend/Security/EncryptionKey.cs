using PasswordManagerLocal.Common.Backend.Constants;
using System.Security.Cryptography;
using static PasswordManagerLocal.Common.Backend.Constants.EncryptionKeyConstants;

namespace PasswordManagerLocal.Common.Backend.Security;

public sealed class EncryptionKey : IDisposable, IEquatable<EncryptionKey>
{
    private readonly byte[] _key;
    private bool _disposed;

    private EncryptionKey(byte[] key)
    {
        _key = key;
    }

    public static EncryptionKey Create()
    {
        var raw = AES256.GenerateKey();
        try
        {
            var copy = new byte[CryptographyConstants.Aes256KeySizeInBytes];
            Buffer.BlockCopy(raw, 0, copy, 0, CryptographyConstants.Aes256KeySizeInBytes);
            return new EncryptionKey(copy);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(raw);
        }
    }

    public static EncryptionKey FromRaw(ReadOnlySpan<byte> raw)
    {
        if (raw.Length != CryptographyConstants.Aes256KeySizeInBytes)
            throw new ArgumentException("Invalid key size.");

        var copy = new byte[CryptographyConstants.Aes256KeySizeInBytes];
        raw.CopyTo(copy);
        return new EncryptionKey(copy);
    }

    public static EncryptionKey FromPassword(ReadOnlySpan<byte> password, ReadOnlySpan<byte> salt, int iterations = Iterations, HashAlgorithmName alg = default)
    {
        if (password.Length == 0)
            throw new ArgumentException("Empty password.");

        if (salt.Length == 0)
            throw new ArgumentException("Empty salt.");

        if (alg == default)
            alg = HashAlgorithmName.SHA512;

        var raw = Rfc2898DeriveBytes.Pbkdf2(password, salt, iterations, alg, CryptographyConstants.Aes256KeySizeInBytes);
        return new EncryptionKey(raw);
    }

    public ReadOnlySpan<byte> AsSpan()
    {
        ThrowIfDisposed();
        return _key.AsSpan();
    }

    internal byte[] ExportCopy()
    {
        ThrowIfDisposed();
        var copy = new byte[_key.Length];
        Buffer.BlockCopy(_key, 0, copy, 0, _key.Length);
        return copy;
    }

    public void Dispose()
    {
        if (_disposed)
            return;

        CryptographicOperations.ZeroMemory(_key);
        _disposed = true;
        GC.SuppressFinalize(this);
    }

    private void ThrowIfDisposed()
    {
        if (_disposed)
            throw new ObjectDisposedException(nameof(EncryptionKey));
    }

    // === IEquatable és operátorok ===

    public bool Equals(EncryptionKey? other)
    {
        if (other is null) return false;
        if (ReferenceEquals(this, other)) return true;
        ThrowIfDisposed();
        other.ThrowIfDisposed();
        return CryptographicOperations.FixedTimeEquals(_key, other._key);
    }

    public override bool Equals(object? obj) => Equals(obj as EncryptionKey);

    public override int GetHashCode()
    {
        ThrowIfDisposed();
        // Egyszerű hash, de nem kriptográfiailag biztonságos. Csak dictionary-hez kell.
        int hash = 17;
        foreach (var b in _key)
            hash = hash * 31 + b;
        return hash;
    }

    public static bool operator ==(EncryptionKey? left, EncryptionKey? right)
    {
        if (left is null) return right is null;
        return left.Equals(right);
    }

    public static bool operator !=(EncryptionKey? left, EncryptionKey? right) => !(left == right);
}