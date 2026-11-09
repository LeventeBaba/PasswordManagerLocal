using PasswordManagerLocal.Common.Backend.Abstractions.Services;
using PasswordManagerLocal.Common.Backend.Security;
using PasswordManagerLocal.Common.Backend.Utils;
using System.Collections.Concurrent;
using System.Security.Cryptography;
using static PasswordManagerLocal.Common.Backend.Constants.TokenConstants;
using PasswordManagerLocal.Common.Backend.State;

namespace PasswordManagerLocal.Common.Backend.Services;

public sealed class KeyVaultService : IKeyVaultService
{
    private readonly ConcurrentDictionary<Guid, KeyVaultEntry> _map = new();
    private readonly object _gate = new();

    public void SetUserKey(Guid token, EncryptionKey key, DateTimeOffset? expiresAt = null)
    {
        if (token == Guid.Empty)
            return;

        var raw = key.ExportCopy();
        try
        {
            var exp = UtcDateTimeUtil.ToUtc(expiresAt ?? DateTimeOffset.UtcNow.Add(LoginTokenExpirationTime));
            var replacement = new KeyVaultEntry(EncryptionKey.FromRaw(raw), exp);

            lock (_gate)
            {
                if (_map.TryGetValue(token, out var old))
                {
                    _map[token] = replacement;
                    old.Dispose();
                    return;
                }

                _map[token] = replacement;
            }
        }
        finally
        {
            CryptographicOperations.ZeroMemory(raw);
        }
    }

    public bool RotateUserKey(Guid token, EncryptionKey newKey, DateTimeOffset? newExpiresAt = null)
    {
        if (token == Guid.Empty)
            return false;

        var raw = newKey.ExportCopy();
        try
        {
            lock (_gate)
            {
                if (!_map.TryGetValue(token, out var entry))
                    return false;

                var replacement = new KeyVaultEntry(
                    EncryptionKey.FromRaw(raw),
                    UtcDateTimeUtil.ToUtc(newExpiresAt ?? entry.ExpiresAt));
                _map[token] = replacement;
                entry.Dispose();
                return true;
            }
        }
        finally
        {
            CryptographicOperations.ZeroMemory(raw);
        }
    }

    public bool HasUserKey(Guid token)
    {
        if (token == Guid.Empty)
            return false;

        lock (_gate)
            return _map.TryGetValue(token, out var entry) && entry.ExpiresAt > DateTimeOffset.UtcNow;
    }

    public void InvalidateToken(Guid token)
    {
        if (token == Guid.Empty)
            return;

        lock (_gate)
        {
            if (_map.TryRemove(token, out var entry))
                entry.Dispose();
        }
    }

    public int PurgeExpired()
    {
        var now = DateTimeOffset.UtcNow;
        var count = 0;

        lock (_gate)
        {
            foreach (var pair in _map.ToArray())
            {
                if (pair.Value.ExpiresAt > now || !_map.TryRemove(pair.Key, out var entry))
                    continue;

                entry.Dispose();
                count++;
            }
        }

        return count;
    }

    public void ClearAll()
    {
        lock (_gate)
        {
            var entries = _map.Values.ToArray();
            _map.Clear();

            foreach (var entry in entries)
                entry.Dispose();
        }
    }

    public bool TryGetEncryptionKey(Guid token, out EncryptionKey key)
    {
        key = default!;

        if (token == Guid.Empty)
            return false;

        lock (_gate)
        {
            if (!_map.TryGetValue(token, out var entry))
                return false;

            if (entry.ExpiresAt <= DateTimeOffset.UtcNow)
            {
                _map.TryRemove(token, out _);
                entry.Dispose();
                return false;
            }

            var raw = entry.Key.ExportCopy();
            try
            {
                key = EncryptionKey.FromRaw(raw);
                return true;
            }
            finally
            {
                CryptographicOperations.ZeroMemory(raw);
            }
        }
    }
}
