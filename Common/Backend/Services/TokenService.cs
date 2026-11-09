using PasswordManagerLocal.Common.Backend.Abstractions.Services;
using PasswordManagerLocal.Common.Backend.Exceptions;
using PasswordManagerLocal.Common.Backend.Models;
using System.Collections.Concurrent;
using System.Security.Cryptography;
using static PasswordManagerLocal.Common.Backend.Constants.TokenConstants;
using PasswordManagerLocal.Common.Backend.State;
using PasswordManagerLocal.Common.Backend.Abstractions.Notifications;
using PasswordManagerLocal.Common.Contracts.Notifications;

namespace PasswordManagerLocal.Common.Backend.Services;

public sealed class TokenService : ITokenService
{
    private readonly ConcurrentDictionary<Guid, TokenEntry> _storage = new();
    private readonly ConcurrentDictionary<Guid, InvalidationEntry> _invalidations = new();
    private readonly IBackendChangePublisher? _changePublisher;
    private readonly TimeProvider _timeProvider;

    public TokenService(
        IBackendChangePublisher? changePublisher = null,
        TimeProvider? timeProvider = null)
    {
        _changePublisher = changePublisher;
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    public Guid Issue(Guid uid)
    {
        for (int i = 0; i <= NumberOfTokenGenerationRetries; i++)
        {
            var token = GenerateToken();

            if (token == Guid.Empty)
                continue;

            var expiresTicksUtc = _timeProvider.GetUtcNow().Add(LoginTokenExpirationTime).UtcDateTime.Ticks;
            var entry = new TokenEntry(uid, expiresTicksUtc);

            if (_storage.TryAdd(token, entry))
            {
                _invalidations.TryRemove(token, out _);
                return token;
            }
        }

        throw new InvalidOperationException("Token generation failed!");
    }

    public bool Validate(Guid token) =>
        TryGetUid(token, out _);

    public bool TryGetUid(Guid token, out Guid uid)
    {
        uid = default;

        if (token == Guid.Empty)
            return false;

        if (!_storage.TryGetValue(token, out var entry))
            return false;

        var nowTicksUtc = GetUtcTicks();

        if (entry.ExpiresTicksUtc <= nowTicksUtc)
        {
            Expire(token, entry);
            return false;
        }

        uid = entry.Uid;
        return true;
    }

    public bool TryGetExpiresAtUtc(Guid token, out DateTimeOffset expiresAtUtc)
    {
        expiresAtUtc = default;

        if (token == Guid.Empty)
            return false;

        if (!_storage.TryGetValue(token, out var entry))
            return false;

        var nowTicksUtc = GetUtcTicks();

        if (entry.ExpiresTicksUtc <= nowTicksUtc)
        {
            Expire(token, entry);
            return false;
        }

        expiresAtUtc = new DateTimeOffset(new DateTime(entry.ExpiresTicksUtc, DateTimeKind.Utc));
        return true;
    }

    public Guid GetUidOrThrow(Guid token)
    {
        if (!TryGetUid(token, out var uid))
            throw new InvalidTokenException("Invalid or expired token.");

        return uid;
    }

    public IReadOnlyList<Guid> ListTokensByUid(Guid uid)
    {
        PurgeExpired();

        if (uid == Guid.Empty)
            return [];

        return _storage
            .Where(kv => kv.Value.Uid == uid)
            .Select(kv => kv.Key)
            .ToList();
    }


    public bool Revoke(Guid token) =>
        Revoke(token, AuthSessionInvalidationReason.LoggedOut);


    public bool Revoke(Guid token, AuthSessionInvalidationReason reason)
    {
        if (token == Guid.Empty)
            return false;

        var removed = _storage.TryRemove(token, out _);

        if (reason != AuthSessionInvalidationReason.None)
        {
            _invalidations[token] = new InvalidationEntry(
                reason,
                _timeProvider.GetUtcNow().Add(InvalidationReasonRetentionTime).UtcDateTime.Ticks);
        }

        return removed;
    }


    public bool TryGetInvalidationReason(Guid token, out AuthSessionInvalidationReason reason)
    {
        reason = AuthSessionInvalidationReason.None;

        if (token == Guid.Empty)
            return false;

        if (!_invalidations.TryGetValue(token, out var entry))
            return false;

        if (entry.ExpiresTicksUtc <= GetUtcTicks())
        {
            _invalidations.TryRemove(token, out _);
            return false;
        }

        reason = entry.Reason;
        return true;
    }


    public int PurgeExpired()
    {
        var nowTicksUtc = GetUtcTicks();
        var removed = 0;
        var changedUsers = new HashSet<Guid>();

        foreach (var kv in _storage)
        {
            if (kv.Value.ExpiresTicksUtc <= nowTicksUtc &&
                ((ICollection<KeyValuePair<Guid, TokenEntry>>)_storage).Remove(kv))
            {
                _invalidations[kv.Key] = new InvalidationEntry(
                    AuthSessionInvalidationReason.Expired,
                    _timeProvider.GetUtcNow().Add(InvalidationReasonRetentionTime).UtcDateTime.Ticks);
                changedUsers.Add(kv.Value.Uid);
                removed++;
            }
        }

        foreach (var kv in _invalidations)
        {
            if (kv.Value.ExpiresTicksUtc <= nowTicksUtc)
                _invalidations.TryRemove(kv.Key, out _);
        }

        foreach (var userId in changedUsers)
            PublishSessionChange(userId);

        return removed;
    }

    public void RevokeAll()
    {
        _storage.Clear();
        _invalidations.Clear();
    }

    private Guid GenerateToken()
    {
        Span<byte> bytes = stackalloc byte[16];
        RandomNumberGenerator.Fill(bytes);
        return new Guid(bytes);
    }

    private void Expire(Guid token, TokenEntry entry)
    {
        if (!((ICollection<KeyValuePair<Guid, TokenEntry>>)_storage).Remove(new(token, entry)))
            return;

        _invalidations[token] = new InvalidationEntry(
            AuthSessionInvalidationReason.Expired,
            _timeProvider.GetUtcNow().Add(InvalidationReasonRetentionTime).UtcDateTime.Ticks);
        PublishSessionChange(entry.Uid);
    }

    private long GetUtcTicks() => _timeProvider.GetUtcNow().UtcDateTime.Ticks;

    private void PublishSessionChange(Guid userId)
    {
        if (userId != Guid.Empty)
            _changePublisher?.Publish(FrontendChangeScope.AuthenticationSessions, userId);
    }

}
