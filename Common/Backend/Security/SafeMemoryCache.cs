using Microsoft.Extensions.Caching.Memory;
using System.Collections.Concurrent;

namespace PasswordManagerLocal.Common.Backend.Security;

public sealed class SafeMemoryCache
{
    private readonly IMemoryCache _cache;
    private readonly ConcurrentDictionary<string, Lazy<Task<object?>>> _inflight = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, byte> _keys = new(StringComparer.Ordinal);
    private readonly object _clearLock = new();
    private CancellationTokenSource _clearCancellation = new();
    private long _clearVersion;

    public SafeMemoryCache(IMemoryCache cache) =>
        _cache = cache ?? throw new ArgumentNullException(nameof(cache));

    public bool TryGet<T>(string key, out T? value) => _cache.TryGetValue(key, out value);

    public void Set<T>(string key, T value, MemoryCacheEntryOptions options)
    {
        lock (_clearLock)
        {
            _keys[key] = 0;
            _cache.Set(key, value, options);
        }
    }

    public async Task<T?> GetOrCreateAsync<T>(
        string key,
        Func<CancellationToken, Task<T?>> factory,
        MemoryCacheEntryOptions options,
        CancellationToken cancellationToken = default)
    {
        if (_cache.TryGetValue(key, out T? value))
            return value;

        CancellationToken clearToken;
        long clearVersion;
        lock (_clearLock)
        {
            clearToken = _clearCancellation.Token;
            clearVersion = _clearVersion;
        }

        var lazy = _inflight.GetOrAdd(key, _ => new Lazy<Task<object?>>(async () =>
        {
            using var linkedCancellation = CancellationTokenSource.CreateLinkedTokenSource(
                clearToken,
                cancellationToken);
            var result = await factory(linkedCancellation.Token);

            lock (_clearLock)
            {
                if (clearVersion != _clearVersion || linkedCancellation.IsCancellationRequested)
                {
                    if (result is IDisposable disposable)
                        disposable.Dispose();

                    linkedCancellation.Token.ThrowIfCancellationRequested();
                    throw new OperationCanceledException("The cache was cleared while the value was loading.");
                }

                if (result is not null)
                {
                    _keys[key] = 0;
                    _cache.Set(key, result, options);
                }
            }

            return result;
        }));

        try
        {
            var result = await lazy.Value.WaitAsync(cancellationToken);
            return (T?)result;
        }
        finally
        {
            ((ICollection<KeyValuePair<string, Lazy<Task<object?>>>>)_inflight)
                .Remove(new KeyValuePair<string, Lazy<Task<object?>>>(key, lazy));
        }
    }

    public void Remove(string key)
    {
        lock (_clearLock)
        {
            _keys.TryRemove(key, out _);
            _cache.Remove(key);
        }
    }

    public void Clear()
    {
        CancellationTokenSource previousCancellation;
        lock (_clearLock)
        {
            previousCancellation = _clearCancellation;
            _clearCancellation = new CancellationTokenSource();
            _clearVersion++;
            _inflight.Clear();

            foreach (var key in _keys.Keys)
            {
                if (_keys.TryRemove(key, out _))
                    _cache.Remove(key);
            }
        }

        try
        {
            previousCancellation.Cancel();
        }
        finally
        {
            previousCancellation.Dispose();
        }
    }
}
