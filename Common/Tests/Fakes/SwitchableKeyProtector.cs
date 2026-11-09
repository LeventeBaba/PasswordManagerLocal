using PasswordManagerLocal.Common.Backend.Abstractions.Security;
using PasswordManagerLocal.Common.Backend.Exceptions;
using PasswordManagerLocal.Common.Backend.Models;
using PasswordManagerLocal.Common.Tests.TestInfrastructure;

namespace PasswordManagerLocal.Common.Tests.Fakes;

public sealed class SwitchableKeyProtector : IKeyProtector
{
    private readonly TestKeyProtector _inner = new();

    public bool IsTemporarilyUnavailable { get; set; }
    public bool IsPermanentlyUnavailable { get; set; }

    public byte[] Protect(ReadOnlySpan<byte> plaintext)
    {
        ThrowIfUnavailable();
        return _inner.Protect(plaintext);
    }

    public byte[] Unprotect(ReadOnlySpan<byte> protectedBlob)
    {
        ThrowIfUnavailable();
        if (IsPermanentlyUnavailable)
            throw new System.Security.Cryptography.CryptographicException("The original platform key is unavailable.");
        return _inner.Unprotect(protectedBlob);
    }

    private void ThrowIfUnavailable()
    {
        if (IsTemporarilyUnavailable)
        {
            throw new KeyProtectorUnavailableException(
                KeyProtectorUnavailableReason.DeviceLocked);
        }
    }
}
