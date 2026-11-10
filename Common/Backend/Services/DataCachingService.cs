using PasswordManagerLocal.Common.Backend.Utils;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Primitives;
using PasswordManagerLocal.Common.Backend.Abstractions.Services;
using PasswordManagerLocal.Common.Backend.Models.Encrypted;
using PasswordManagerLocal.Common.Backend.Security;
using System.Collections.Concurrent;
using static PasswordManagerLocal.Common.Backend.Constants.DataCachingConstants;

namespace PasswordManagerLocal.Common.Backend.Services;

public sealed class DataCachingService : IDataCachingService
{
    private readonly SafeMemoryCache _cache;
    private readonly ITokenService _tokens;
    private readonly TimeSpan _userTtl;

    private readonly ConcurrentDictionary<Guid, CancellationTokenSource> _tokenCts = new();
    private readonly ConcurrentDictionary<string, UserDataBundle> _currentByKey = new();
    private readonly ConcurrentDictionary<string, byte> _removeWithoutDisposeKeys = new();
    private readonly object _stateLock = new();

    public DataCachingService(SafeMemoryCache cache, ITokenService tokens)
        : this(cache, tokens, UserDataCacheExpirationTime)
    {
    }

    public DataCachingService(SafeMemoryCache cache, ITokenService tokens, TimeSpan userTtl)
    {
        _cache = cache ?? throw new ArgumentNullException(nameof(cache));
        _tokens = tokens ?? throw new ArgumentNullException(nameof(tokens));
        _userTtl = userTtl;
    }

    private string UserKey(Guid token) => $"t:{token:N}:user";

    private bool TryStoreValue(
        Guid token,
        string key,
        UserDataBundle value,
        TimeSpan ttl)
    {
        lock (_stateLock)
        {
            if (!_tokens.Validate(token))
                return false;

            if (_currentByKey.TryGetValue(key, out var previous) && !ReferenceEquals(previous, value))
                previous.Dispose();
            _currentByKey[key] = value;
            _cache.Set(key, value, EntryOptions(token, ttl));
            return true;
        }
    }

    private void RemoveCurrentWithoutDispose(string key)
    {
        if (_currentByKey.TryRemove(key, out var removed))
            removed.Dispose();
        _removeWithoutDisposeKeys[key] = 0;
        _cache.Remove(key);
    }

    private MemoryCacheEntryOptions EntryOptions(Guid token, TimeSpan ttl)
    {
        if (!_tokenCts.TryGetValue(token, out var cts))
        {
            var candidate = new CancellationTokenSource();
            cts = _tokenCts.GetOrAdd(token, candidate);
            if (!ReferenceEquals(cts, candidate))
                candidate.Dispose();
        }

        var expiresAt = DateTimeOffset.UtcNow.Add(ttl);

        if (_tokens.TryGetExpiresAtUtc(token, out var tokenExpiresAt) && tokenExpiresAt < expiresAt)
            expiresAt = tokenExpiresAt;

        var opts = new MemoryCacheEntryOptions
        {
            AbsoluteExpiration = expiresAt,
            Size = 1
        };

        opts.AddExpirationToken(new CancellationChangeToken(cts.Token));

        opts.RegisterPostEvictionCallback((key, value, reason, state) =>
        {
            if (key is not string skey)
                return;

            var self = (DataCachingService)state!;

            if (self._removeWithoutDisposeKeys.TryRemove(skey, out _))
            {
                if (self._currentByKey.TryGetValue(skey, out var skippedCurrent) && ReferenceEquals(skippedCurrent, value))
                    self._currentByKey.TryRemove(skey, out _);

                return;
            }

            if (reason == EvictionReason.Replaced)
            {
                if (self._currentByKey.TryGetValue(skey, out var current) && ReferenceEquals(current, value))
                    return;

                return;
            }

            lock (self._stateLock)
            {
                if (self._currentByKey.TryGetValue(skey, out var cur) && ReferenceEquals(cur, value))
                {
                    self._currentByKey.TryRemove(skey, out _);
                    cur.Dispose();
                }
            }
        }, this);

        return opts;
    }

    public bool TryGetUserDataBundle(Guid token, out UserDataBundle? value)
    {
        value = default;

        if (!_tokens.Validate(token))
        {
            InvalidateToken(token);
            return false;
        }

        var key = UserKey(token);
        lock (_stateLock)
        {
            if (!_cache.TryGet(key, out object? current) || current is not UserDataBundle bundle)
                return false;
            value = UserDataBundleCopy.Clone(bundle);
            return true;
        }
    }

    public void SetUserDataBundle(Guid token, UserDataBundle value)
    {
        var copy = UserDataBundleCopy.Clone(value);
        if (!TryStoreValue(token, UserKey(token), copy, _userTtl))
        {
            copy.Dispose();
            InvalidateToken(token);
        }
    }

    public void InvalidateToken(Guid token)
    {
        CancellationTokenSource? cancellation = null;
        lock (_stateLock)
        {
            var prefix = $"t:{token:N}:";

            foreach (var key in _currentByKey.Keys)
            {
                if (key.StartsWith(prefix, StringComparison.Ordinal))
                    RemoveCurrentWithoutDispose(key);
            }

            _tokenCts.TryRemove(token, out cancellation);
        }

        if (cancellation is not null)
        {
            try
            {
                cancellation.Cancel();
            }
            finally
            {
                cancellation.Dispose();
            }
        }
    }

    public void ClearAll()
    {
        var cancellationSources = new List<CancellationTokenSource>();
        var valuesToDispose = new HashSet<UserDataBundle>(ReferenceEqualityComparer.Instance);
        var failures = new List<Exception>();

        lock (_stateLock)
        {
            foreach (var token in _tokenCts.Keys)
            {
                if (_tokenCts.TryRemove(token, out var cancellation))
                    cancellationSources.Add(cancellation);
            }

            foreach (var value in _currentByKey.Values)
                valuesToDispose.Add(value);

            try
            {
                _cache.Clear();
            }
            catch (Exception exception)
            {
                failures.Add(exception);
            }

            _currentByKey.Clear();
            _removeWithoutDisposeKeys.Clear();
        }

        foreach (var cancellation in cancellationSources)
        {
            try
            {
                cancellation.Cancel();
            }
            catch (Exception exception)
            {
                failures.Add(exception);
            }
            finally
            {
                cancellation.Dispose();
            }
        }

        foreach (var value in valuesToDispose)
        {
            try
            {
                value.Dispose();
            }
            catch (Exception exception)
            {
                failures.Add(exception);
            }
        }

        if (failures.Count > 0)
            throw new AggregateException(failures);
    }
}