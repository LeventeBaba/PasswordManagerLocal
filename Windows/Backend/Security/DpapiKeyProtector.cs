using PasswordManagerLocal.Common.Backend.Abstractions.Security;
using System;
using System.Runtime.Versioning;
using System.Security.Cryptography;

namespace PasswordManagerLocal.Windows.Backend.Security;

[SupportedOSPlatform("windows")]
public sealed class DpapiKeyProtector : IKeyProtector
{
    public byte[] Protect(ReadOnlySpan<byte> plaintext)
    => ProtectedData.Protect(plaintext.ToArray(), null, DataProtectionScope.CurrentUser);

    public byte[] Unprotect(ReadOnlySpan<byte> protectedBlob)
        => ProtectedData.Unprotect(protectedBlob.ToArray(), null, DataProtectionScope.CurrentUser);


}