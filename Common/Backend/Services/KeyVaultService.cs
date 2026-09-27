using PasswordManagerLocal.Common.Backend.Abstractions.Services;
using PasswordManagerLocal.Common.Backend.Models.Encrypted;
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
                    CopyBlobKeys(old, replacement);
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

    public void SetUserBlobKeys(Guid token, UserData userData)
    {
        if (token == Guid.Empty)
            return;

        lock (_gate)
        {
            if (!_map.TryGetValue(token, out var entry))
                return;

            if (entry.ExpiresAt <= DateTimeOffset.UtcNow)
            {
                _map.TryRemove(token, out _);
                entry.Dispose();
                return;
            }

            ReplaceBlobKey(ref entry.GeneralUserDataKey, userData.GeneralUserDataKey);
            ReplaceBlobKey(ref entry.UserPasswordsDataKey, userData.UserPasswordsDataKey);
            ReplaceBlobKey(ref entry.UserDevicesDataKey, userData.UserDevicesDataKey);
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
                CopyBlobKeys(entry, replacement);
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

    public bool TryGetEncryptionKey(Guid token, out EncryptionKey key) =>
        TryGetKey(token, entry => entry.Key, out key);

    public bool TryGetGeneralUserDataKey(Guid token, out EncryptionKey key) =>
        TryGetKey(token, entry => entry.GeneralUserDataKey, out key);

    public bool TryGetUserPasswordsDataKey(Guid token, out EncryptionKey key) =>
        TryGetKey(token, entry => entry.UserPasswordsDataKey, out key);

    public bool TryGetUserDevicesDataKey(Guid token, out EncryptionKey key) =>
        TryGetKey(token, entry => entry.UserDevicesDataKey, out key);

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

    private bool TryGetKey(Guid token, Func<KeyVaultEntry, EncryptionKey?> selector, out EncryptionKey key)
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

            var source = selector(entry);
            if (source is null)
                return false;

            var raw = source.ExportCopy();
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

    private void ReplaceBlobKey(ref EncryptionKey? target, byte[] raw)
    {
        target?.Dispose();
        target = raw.Length == 0 ? null : EncryptionKey.FromRaw(raw);
    }

    private void CopyBlobKeys(KeyVaultEntry source, KeyVaultEntry target)
    {
        CopyBlobKey(source.GeneralUserDataKey, ref target.GeneralUserDataKey);
        CopyBlobKey(source.UserPasswordsDataKey, ref target.UserPasswordsDataKey);
        CopyBlobKey(source.UserDevicesDataKey, ref target.UserDevicesDataKey);
    }

    private void CopyBlobKey(EncryptionKey? source, ref EncryptionKey? target)
    {
        if (source is null)
            return;

        var raw = source.ExportCopy();
        try
        {
            target = EncryptionKey.FromRaw(raw);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(raw);
        }
    }
}
