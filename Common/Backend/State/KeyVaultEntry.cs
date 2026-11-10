using PasswordManagerLocal.Common.Backend.Security;
using PasswordManagerLocal.Common.Backend.Utils;

namespace PasswordManagerLocal.Common.Backend.State;

internal sealed class KeyVaultEntry : IDisposable
{
    public EncryptionKey Key;
    public DateTimeOffset ExpiresAt;

    public KeyVaultEntry(EncryptionKey key, DateTimeOffset exp)
    {
        Key = key;
        ExpiresAt = UtcDateTimeUtil.ToUtc(exp);
    }

    public void Dispose()
    {
        Key.Dispose();
    }
}
