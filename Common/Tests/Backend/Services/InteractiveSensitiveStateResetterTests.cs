using Microsoft.Extensions.Caching.Memory;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using PasswordManagerLocal.Common.Backend.Abstractions.Services;
using PasswordManagerLocal.Common.Backend.Models.Encrypted;
using PasswordManagerLocal.Common.Backend.Security;
using PasswordManagerLocal.Common.Backend.Services;

namespace PasswordManagerLocal.Common.Tests.Backend.Services;

[TestClass]
public sealed class InteractiveSensitiveStateResetterTests
{
    [TestMethod]
    public async Task ResetRevokesTokensDisposesKeysClearsCachesAndIsIdempotent()
    {
        using var memoryCache = new MemoryCache(new MemoryCacheOptions { SizeLimit = 100 });
        var safeMemoryCache = new SafeMemoryCache(memoryCache);
        ITokenService tokens = new TokenService();
        IKeyVaultService keys = new KeyVaultService();
        IDataCachingService cache = new DataCachingService(safeMemoryCache, tokens);
        var resetter = new InteractiveSensitiveStateResetter(tokens, cache, keys, safeMemoryCache);
        var token = tokens.Issue(Guid.NewGuid());
        using var key = EncryptionKey.FromRaw(Enumerable.Repeat((byte)7, 32).ToArray());
        var userData = CreateUserData();

        keys.SetUserKey(token, key, DateTimeOffset.UtcNow.AddMinutes(5));
        cache.SetUserData(token, userData);
        Assert.IsTrue(tokens.Validate(token));
        Assert.IsTrue(keys.HasUserKey(token));
        Assert.IsTrue(cache.TryGetUserData(token, out _));

        await resetter.ResetAsync();
        await resetter.ResetAsync();

        Assert.IsFalse(tokens.Validate(token));
        Assert.IsFalse(keys.HasUserKey(token));
        Assert.IsFalse(cache.TryGetUserData(token, out _));
    }

    [TestMethod]
    public async Task CacheClearCancelsInflightPopulation()
    {
        using var memoryCache = new MemoryCache(new MemoryCacheOptions { SizeLimit = 100 });
        var safeMemoryCache = new SafeMemoryCache(memoryCache);
        ITokenService tokens = new TokenService();
        IDataCachingService cache = new DataCachingService(safeMemoryCache, tokens);
        var token = tokens.Issue(Guid.NewGuid());
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        var loading = cache.GetOrLoadUserDataAsync(token, async cancellationToken =>
        {
            started.TrySetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            return CreateUserData();
        });

        await started.Task;
        cache.ClearAll();

        await Assert.ThrowsAsync<OperationCanceledException>(async () => await loading);
    }

    [TestMethod]
    public async Task ClearPreventsStaleInflightValueFromRepopulatingCache()
    {
        using var memoryCache = new MemoryCache(new MemoryCacheOptions { SizeLimit = 100 });
        var safeMemoryCache = new SafeMemoryCache(memoryCache);
        ITokenService tokens = new TokenService();
        IDataCachingService cache = new DataCachingService(safeMemoryCache, tokens);
        var token = tokens.Issue(Guid.NewGuid());
        var started = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseLoader = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);

        var loading = cache.GetOrLoadUserDataAsync(token, async _ =>
        {
            started.TrySetResult();
            await releaseLoader.Task;
            return CreateUserData();
        });

        await started.Task;
        cache.ClearAll();
        releaseLoader.TrySetResult();

        await Assert.ThrowsAsync<OperationCanceledException>(async () => await loading);
        Assert.IsFalse(cache.TryGetUserData(token, out _));
    }

    [TestMethod]
    public async Task StaleInflightCompletionCannotRemoveReplacementInflightLoad()
    {
        using var memoryCache = new MemoryCache(new MemoryCacheOptions { SizeLimit = 100 });
        var safeMemoryCache = new SafeMemoryCache(memoryCache);
        var options = new MemoryCacheEntryOptions { Size = 1 };
        var oldStarted = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseOld = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var newStarted = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseNew = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var replacement = CreateUserData();
        var replacementLoads = 0;

        var staleLoad = safeMemoryCache.GetOrCreateAsync<UserData>(
            "user",
            async _ =>
            {
                oldStarted.TrySetResult();
                await releaseOld.Task;
                return CreateUserData();
            },
            options);

        await oldStarted.Task;
        safeMemoryCache.Clear();

        var replacementLoad = safeMemoryCache.GetOrCreateAsync<UserData>(
            "user",
            async _ =>
            {
                Interlocked.Increment(ref replacementLoads);
                newStarted.TrySetResult();
                await releaseNew.Task;
                return replacement;
            },
            options);

        await newStarted.Task;
        releaseOld.TrySetResult();
        await Assert.ThrowsAsync<OperationCanceledException>(async () => await staleLoad);

        var joinedLoad = safeMemoryCache.GetOrCreateAsync<UserData>(
            "user",
            _ =>
            {
                Interlocked.Increment(ref replacementLoads);
                return Task.FromResult<UserData?>(CreateUserData());
            },
            options);

        Assert.AreEqual(1, Volatile.Read(ref replacementLoads));
        Assert.IsFalse(joinedLoad.IsCompleted);

        releaseNew.TrySetResult();
        Assert.AreSame(replacement, await replacementLoad);
        Assert.AreSame(replacement, await joinedLoad);
        Assert.AreEqual(1, Volatile.Read(ref replacementLoads));
    }

    private static UserData CreateUserData()
    {
        var data = new UserData
        {
            UId = Guid.NewGuid(),
            GeneralUserDataKey = Enumerable.Repeat((byte)1, 32).ToArray(),
            GeneralUserDataIntegrityHash = Enumerable.Repeat((byte)2, 32).ToArray(),
            UserPasswordsDataKey = Enumerable.Repeat((byte)3, 32).ToArray(),
            UserPasswordsDataIntegrityHash = Enumerable.Repeat((byte)4, 32).ToArray(),
            UserDevicesDataKey = Enumerable.Repeat((byte)5, 32).ToArray(),
            UserDevicesDataIntegrityHash = Enumerable.Repeat((byte)6, 32).ToArray()
        };
        data.GenerateIntegrityHash();
        return data;
    }
}
