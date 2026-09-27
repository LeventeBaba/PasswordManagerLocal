using PasswordManagerLocal.Common.Backend.Abstractions.Security;

namespace PasswordManagerLocal.Common.Tests.TestInfrastructure;

public sealed class TestKeyProtector : IKeyProtector
{
    public byte[]? LastUnprotectedBuffer { get; private set; }

    public byte[] Protect(ReadOnlySpan<byte> plaintext)
    {
        var result = new byte[plaintext.Length + 1];
        result[0] = 0xA5;

        for (int i = 0; i < plaintext.Length; i++)
            result[i + 1] = (byte)(plaintext[i] ^ 0x5A);

        return result;
    }

    public byte[] Unprotect(ReadOnlySpan<byte> protectedBlob)
    {
        if (protectedBlob.Length < 1 || protectedBlob[0] != 0xA5)
            throw new InvalidOperationException("Invalid protected blob.");

        var result = new byte[protectedBlob.Length - 1];

        for (int i = 0; i < result.Length; i++)
            result[i] = (byte)(protectedBlob[i + 1] ^ 0x5A);

        LastUnprotectedBuffer = result;
        return result;
    }
}