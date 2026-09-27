using PasswordManagerLocal.Common.Backend.Abstractions.Services;
using PasswordManagerLocal.Common.Backend.Security;

namespace PasswordManagerLocal.Common.Backend.Services;

public sealed class InteractiveSensitiveStateResetter : IInteractiveSensitiveStateResetter
{
    private readonly ITokenService _tokens;
    private readonly IDataCachingService _cache;
    private readonly IKeyVaultService _keys;
    private readonly SafeMemoryCache _memoryCache;

    public InteractiveSensitiveStateResetter(
        ITokenService tokens,
        IDataCachingService cache,
        IKeyVaultService keys,
        SafeMemoryCache memoryCache)
    {
        _tokens = tokens ?? throw new ArgumentNullException(nameof(tokens));
        _cache = cache ?? throw new ArgumentNullException(nameof(cache));
        _keys = keys ?? throw new ArgumentNullException(nameof(keys));
        _memoryCache = memoryCache ?? throw new ArgumentNullException(nameof(memoryCache));
    }

    public Task ResetAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var failures = new List<Exception>();

        try
        {
            _tokens.RevokeAll();
        }
        catch (Exception exception)
        {
            failures.Add(exception);
        }

        try
        {
            _cache.ClearAll();
        }
        catch (Exception exception)
        {
            failures.Add(exception);
        }

        try
        {
            _keys.ClearAll();
        }
        catch (Exception exception)
        {
            failures.Add(exception);
        }

        try
        {
            _memoryCache.Clear();
        }
        catch (Exception exception)
        {
            failures.Add(exception);
        }

        if (failures.Count > 0)
            throw new AggregateException(failures);

        return Task.CompletedTask;
    }
}
