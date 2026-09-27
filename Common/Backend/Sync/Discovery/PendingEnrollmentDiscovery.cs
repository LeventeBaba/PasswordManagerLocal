using PasswordManagerLocal.Common.Backend.Sync.Enrollment;
using System.Collections.Concurrent;
using System.Security.Cryptography;

namespace PasswordManagerLocal.Common.Backend.Sync.Discovery;

internal sealed class PendingEnrollmentDiscovery : IDisposable
{
    private readonly ConcurrentDictionary<string, DateTimeOffset> _outstandingNonces = new(StringComparer.Ordinal);

    public required string SessionId { get; init; }
    public required byte[] Secret { get; init; }
    public TaskCompletionSource<IReadOnlyList<EnrollmentEndpoint>> Completion { get; } =
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    public void AddNonce(byte[] nonce, DateTimeOffset expiresAt) =>
        _outstandingNonces[Convert.ToHexString(nonce)] = expiresAt;

    public bool ContainsNonce(byte[] nonce, DateTimeOffset now)
    {
        var key = Convert.ToHexString(nonce);
        if (!_outstandingNonces.TryGetValue(key, out var expiresAt) || expiresAt < now)
            return false;

        return true;
    }

    public void PruneNonces(DateTimeOffset now)
    {
        foreach (var item in _outstandingNonces)
        {
            if (item.Value < now)
                _outstandingNonces.TryRemove(item.Key, out _);
        }
    }

    public void Dispose()
    {
        _outstandingNonces.Clear();
        if (Secret.Length > 0)
            CryptographicOperations.ZeroMemory(Secret);
    }
}
