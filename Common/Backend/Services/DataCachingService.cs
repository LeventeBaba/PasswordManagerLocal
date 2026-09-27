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
    private readonly TimeSpan _groupTtl;

    private readonly ConcurrentDictionary<Guid, CancellationTokenSource> _tokenCts = new();
    private readonly ConcurrentDictionary<string, object> _currentByKey = new();
    private readonly ConcurrentDictionary<string, byte> _removeWithoutDisposeKeys = new();
    private readonly object _stateLock = new();
    private long _clearVersion;

    public DataCachingService(SafeMemoryCache cache, ITokenService tokens)
        : this(cache, tokens, UserDataCacheExpirationTime, GroupDataCacheExpirationTime)
    {
    }

    public DataCachingService(SafeMemoryCache cache, ITokenService tokens, TimeSpan userTtl, TimeSpan groupTtl)
    {
        _cache = cache ?? throw new ArgumentNullException(nameof(cache));
        _tokens = tokens ?? throw new ArgumentNullException(nameof(tokens));
        _userTtl = userTtl;
        _groupTtl = groupTtl;
    }

    private string UserKey(Guid token) => $"t:{token:N}:user";
    private string GroupKey(Guid token, Guid groupId) => $"t:{token:N}:g:{groupId:N}";

    private bool TryPrepareCacheOperation(
        Guid token,
        TimeSpan ttl,
        out MemoryCacheEntryOptions options,
        out long clearVersion)
    {
        lock (_stateLock)
        {
            if (!_tokens.Validate(token))
            {
                options = null!;
                clearVersion = 0;
                return false;
            }

            options = EntryOptions(token, ttl);
            clearVersion = _clearVersion;
            return true;
        }
    }

    private bool TryStoreValue(
        Guid token,
        string key,
        object value,
        TimeSpan ttl)
    {
        lock (_stateLock)
        {
            if (!_tokens.Validate(token))
                return false;

            _currentByKey[key] = value;
            _cache.Set(key, value, EntryOptions(token, ttl));
            return true;
        }
    }

    private void DisposeCurrentAndRemove(string key)
    {
        RemoveCurrentWithoutDispose(key);
    }

    private void RemoveCurrentWithoutDispose(string key)
    {
        _currentByKey.TryRemove(key, out _);
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

            if (self._currentByKey.TryGetValue(skey, out var cur) && ReferenceEquals(cur, value))
            {
                self._currentByKey.TryRemove(skey, out _);
            }
        }, this);

        return opts;
    }

    public async Task<UserDataBundle?> GetOrLoadUserDataBundleAsync(Guid token, Func<CancellationToken, Task<UserDataBundle?>> loader, CancellationToken ct = default)
    {
        if (!TryPrepareCacheOperation(token, _userTtl, out var options, out var clearVersion))
        {
            InvalidateToken(token);
            return default;
        }

        var key = UserKey(token);
        var result = await _cache.GetOrCreateAsync(key, loader, options, ct);
        return TrackLoadedValue(token, key, result, clearVersion);
    }

    public Task<UserDataBundle?> GetOrLoadUserDataBundleAsync(Guid token, Func<Task<UserDataBundle?>> loader)
        => GetOrLoadUserDataBundleAsync(token, _ => loader());

    public bool TryGetUserDataBundle(Guid token, out UserDataBundle? value)
    {
        value = default;

        if (!_tokens.Validate(token))
        {
            InvalidateToken(token);
            return false;
        }

        var key = UserKey(token);
        if (!_cache.TryGet(key, out object? cached) || cached is null)
            return false;

        if (cached is UserDataBundle bundle)
        {
            value = bundle;
            _currentByKey[key] = bundle;
            return true;
        }

        return false;
    }

    public void SetUserDataBundle(Guid token, UserDataBundle value)
    {
        if (!TryStoreValue(token, UserKey(token), value, _userTtl))
            InvalidateToken(token);
    }

    public async Task<UserData?> GetOrLoadUserDataAsync(Guid token, Func<CancellationToken, Task<UserData?>> loader, CancellationToken ct = default)
    {
        if (!TryPrepareCacheOperation(token, _userTtl, out var options, out var clearVersion))
        {
            InvalidateToken(token);
            return default;
        }

        if (TryGetUserData(token, out var cached))
            return cached;

        var key = UserKey(token);
        var result = await _cache.GetOrCreateAsync(key, loader, options, ct);
        return TrackLoadedValue(token, key, result, clearVersion);
    }

    public Task<UserData?> GetOrLoadUserDataAsync(Guid token, Func<Task<UserData?>> loader)
        => GetOrLoadUserDataAsync(token, _ => loader());

    public bool TryGetUserData(Guid token, out UserData? value)
    {
        value = default;

        if (!_tokens.Validate(token))
        {
            InvalidateToken(token);
            return false;
        }

        var key = UserKey(token);
        if (!_cache.TryGet(key, out object? cached) || cached is null)
            return false;

        if (cached is UserDataBundle bundle)
        {
            value = bundle.UserData;
            _currentByKey[key] = bundle;
            return true;
        }

        if (cached is UserData userData)
        {
            value = userData;
            _currentByKey[key] = userData;
            return true;
        }

        return false;
    }

    public void SetUserData(Guid token, UserData value)
    {
        if (!TryStoreValue(token, UserKey(token), value, _userTtl))
            InvalidateToken(token);
    }

    public async Task<GroupData?> GetOrLoadGroupDataAsync(Guid token, Guid groupId, Func<CancellationToken, Task<GroupData?>> loader, CancellationToken ct = default)
    {
        if (!TryPrepareCacheOperation(token, _groupTtl, out var options, out var clearVersion))
        {
            InvalidateToken(token);
            return default;
        }

        var key = GroupKey(token, groupId);
        var result = await _cache.GetOrCreateAsync(key, loader, options, ct);
        return TrackLoadedValue(token, key, result, clearVersion);
    }

    public Task<GroupData?> GetOrLoadGroupDataAsync(Guid token, Guid groupId, Func<Task<GroupData?>> loader)
        => GetOrLoadGroupDataAsync(token, groupId, _ => loader());

    public bool TryGetGroupData(Guid token, Guid groupId, out GroupData? value)
    {
        if (!_tokens.Validate(token))
        {
            InvalidateToken(token);
            value = default;
            return false;
        }

        var key = GroupKey(token, groupId);
        var ok = _cache.TryGet(key, out value);

        if (ok && value != null)
            _currentByKey[key] = value;

        return ok;
    }

    public void SetGroupData(Guid token, Guid groupId, GroupData value)
    {
        if (!TryStoreValue(token, GroupKey(token, groupId), value, _groupTtl))
            InvalidateToken(token);
    }

    private T? TrackLoadedValue<T>(Guid token, string key, T? value, long clearVersion)
        where T : class
    {
        if (value is null)
            return null;

        var discard = false;
        lock (_stateLock)
        {
            if (clearVersion != _clearVersion || !_tokens.Validate(token))
            {
                _cache.Remove(key);
                discard = true;
            }
            else
            {
                _currentByKey[key] = value;
            }
        }

        if (discard && value is IDisposable disposable)
            disposable.Dispose();

        return discard ? null : value;
    }

    public void InvalidateGroup(Guid token, Guid groupId)
    {
        DisposeCurrentAndRemove(GroupKey(token, groupId));
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
        var valuesToDispose = new HashSet<object>(ReferenceEqualityComparer.Instance);
        var failures = new List<Exception>();

        lock (_stateLock)
        {
            _clearVersion++;

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
            if (value is not IDisposable disposable)
                continue;

            try
            {
                disposable.Dispose();
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