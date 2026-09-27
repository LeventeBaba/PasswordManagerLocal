using PasswordManagerLocal.Common.Backend.Constants;

namespace PasswordManagerLocal.Common.Backend.Sync.Discovery;

internal sealed class RecentLocalDiscoveryNonceCache
{
    private readonly object _lock = new();
    private readonly Dictionary<string, DateTimeOffset> _seen = new(StringComparer.Ordinal);
    private readonly Queue<string> _order = new();

    public bool TryAdd(string scope, byte[] nonce, DateTimeOffset now)
    {
        var key = $"{scope}:{Convert.ToHexString(nonce)}";

        lock (_lock)
        {
            PruneExpired(now);
            if (_seen.ContainsKey(key))
                return false;

            _seen[key] = now.AddSeconds(SyncConstants.LocalDiscoveryReplayRetentionSeconds);
            _order.Enqueue(key);

            while (_seen.Count > SyncConstants.LocalDiscoveryRecentNonceCapacity && _order.Count > 0)
            {
                var oldest = _order.Dequeue();
                _seen.Remove(oldest);
            }

            return true;
        }
    }


    public void Clear()
    {
        lock (_lock)
        {
            _seen.Clear();
            _order.Clear();
        }
    }


    private void PruneExpired(DateTimeOffset now)
    {
        while (_order.Count > 0)
        {
            var key = _order.Peek();
            if (!_seen.TryGetValue(key, out var expiresAt))
            {
                _order.Dequeue();
                continue;
            }

            if (expiresAt >= now)
                break;

            _order.Dequeue();
            _seen.Remove(key);
        }
    }
}
