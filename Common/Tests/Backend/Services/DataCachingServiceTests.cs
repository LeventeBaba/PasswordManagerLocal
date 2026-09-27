using global::PasswordManagerLocal.Common.Tests.TestInfrastructure;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using PasswordManagerLocal.Common.Backend.Abstractions.Services;
using PasswordManagerLocal.Common.Backend.Models.Encrypted;

using MSTestAssert = Microsoft.VisualStudio.TestTools.UnitTesting.Assert;

namespace PasswordManagerLocal.Common.Tests.Backend.Services;

[TestClass]
public sealed class DataCachingServiceTests
{
    [TestMethod]
    [TestCategory("Backend")]
    [TestCategory("Unit")]
    public async Task InvalidateToken_ForcesReload()
    {
        using var host = new BackendTestHost();
        var tokens = host.Services.GetRequiredService<ITokenService>();
        var cache = host.Services.GetRequiredService<IDataCachingService>();
        var token = tokens.Issue(Guid.NewGuid());
        var calls = 0;

        Task<UserData?> Loader()
        {
            calls++;
            var data = CreateUserData("reload");
            return Task.FromResult<UserData?>(data);
        }

        var first = await cache.GetOrLoadUserDataAsync(token, Loader);
        var second = await cache.GetOrLoadUserDataAsync(token, Loader);

        MSTestAssert.IsNotNull(first);
        MSTestAssert.IsNotNull(second);
        MSTestAssert.AreEqual(1, calls);

        cache.InvalidateToken(token);
        var afterInvalidation = await cache.GetOrLoadUserDataAsync(token, Loader);

        MSTestAssert.IsNotNull(afterInvalidation);
        MSTestAssert.AreEqual(2, calls);
    }

    [TestMethod]
    [TestCategory("Backend")]
    [TestCategory("Unit")]
    public void InvalidateGroup_RemovesGroupDataFromCache()
    {
        using var host = new BackendTestHost();
        var tokens = host.Services.GetRequiredService<ITokenService>();
        var cache = host.Services.GetRequiredService<IDataCachingService>();
        var token = tokens.Issue(Guid.NewGuid());
        var groupId = Guid.NewGuid();
        var group = new GroupData
        {
            Id = groupId,
            Name = "g",
            Description = "desc",
            CreatedAt = DateTime.UtcNow,
            LastUpdatedAt = DateTime.UtcNow
        };
        group.Passwords.GenerateIntegrityHash();
        group.GenerateIntegrityHash();

        cache.SetGroupData(token, groupId, group);
        MSTestAssert.IsTrue(cache.TryGetGroupData(token, groupId, out var before));
        MSTestAssert.AreSame(group, before);

        cache.InvalidateGroup(token, groupId);

        MSTestAssert.IsFalse(cache.TryGetGroupData(token, groupId, out var after));
        MSTestAssert.IsNull(after);
        MSTestAssert.AreEqual(groupId, group.Id);
        MSTestAssert.AreEqual("g", group.Name);
    }

    [TestMethod]
    [TestCategory("Backend")]
    [TestCategory("Unit")]
    public async Task ConcurrentLoads_ForSameToken_InvokeLoaderOnlyOnce()
    {
        using var host = new BackendTestHost();
        var tokens = host.Services.GetRequiredService<ITokenService>();
        var cache = host.Services.GetRequiredService<IDataCachingService>();
        var token = tokens.Issue(Guid.NewGuid());
        var gate = new TaskCompletionSource<UserData?>(TaskCreationOptions.RunContinuationsAsynchronously);
        var calls = 0;
        var expected = CreateUserData("concurrent");

        Task<UserData?> Loader(CancellationToken _)
        {
            Interlocked.Increment(ref calls);
            return gate.Task;
        }

        var loads = Enumerable.Range(0, 20)
            .Select(_ => cache.GetOrLoadUserDataAsync(token, Loader))
            .ToArray();
        await WaitUntilAsync(() => Volatile.Read(ref calls) == 1);
        gate.SetResult(expected);
        var results = await Task.WhenAll(loads);

        MSTestAssert.AreEqual(1, calls);
        MSTestAssert.IsTrue(results.All(result => ReferenceEquals(expected, result)));
    }

    [TestMethod]
    [TestCategory("Backend")]
    [TestCategory("Unit")]
    public async Task InvalidToken_DoesNotInvokeLoaderOrCacheData()
    {
        using var host = new BackendTestHost();
        var cache = host.Services.GetRequiredService<IDataCachingService>();
        var calls = 0;

        var result = await cache.GetOrLoadUserDataAsync(Guid.NewGuid(), _ =>
        {
            calls++;
            return Task.FromResult<UserData?>(CreateUserData("invalid"));
        });

        MSTestAssert.IsNull(result);
        MSTestAssert.AreEqual(0, calls);
    }

    private static UserData CreateUserData(string username)
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

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        var timeoutAt = DateTime.UtcNow.AddSeconds(3);
        while (!condition())
        {
            if (DateTime.UtcNow >= timeoutAt)
                MSTestAssert.Fail("Timed out waiting for concurrent cache loading.");

            await Task.Delay(10);
        }
    }
}
