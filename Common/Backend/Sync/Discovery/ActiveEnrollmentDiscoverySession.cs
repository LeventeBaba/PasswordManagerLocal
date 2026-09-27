using System.Security.Cryptography;

namespace PasswordManagerLocal.Common.Backend.Sync.Discovery;

internal sealed class ActiveEnrollmentDiscoverySession : IDisposable
{
    public required string SessionId { get; init; }
    public required byte[] Secret { get; init; }
    public required DateTimeOffset ExpiresAt { get; init; }

    public void Dispose()
    {
        if (Secret.Length > 0)
            CryptographicOperations.ZeroMemory(Secret);
    }
}
