namespace PasswordManagerLocal.Common.Backend.Abstractions.Security;

public interface IKeyProtector
{
    byte[] Protect(ReadOnlySpan<byte> plaintext);
    byte[] Unprotect(ReadOnlySpan<byte> protectedBlob);
}