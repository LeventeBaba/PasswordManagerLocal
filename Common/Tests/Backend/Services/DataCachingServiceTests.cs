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
    public void InvalidateToken_RemovesCachedBundle()
    {
        using var host = new BackendTestHost();
        var tokens = host.Services.GetRequiredService<ITokenService>();
        var cache = host.Services.GetRequiredService<IDataCachingService>();
        var token = tokens.Issue(Guid.NewGuid());
        using var bundle = new UserDataBundle();

        cache.SetUserDataBundle(token, bundle);

        MSTestAssert.IsTrue(cache.TryGetUserDataBundle(token, out var cachedBundle));
        using var cached = cachedBundle;
        MSTestAssert.IsNotNull(cached);
        MSTestAssert.AreNotSame(bundle, cached);

        cache.InvalidateToken(token);
        MSTestAssert.IsFalse(cache.TryGetUserDataBundle(token, out _));
    }

    [TestMethod]
    [TestCategory("Backend")]
    [TestCategory("Unit")]
    public void InvalidToken_DoesNotCacheBundle()
    {
        using var host = new BackendTestHost();
        var cache = host.Services.GetRequiredService<IDataCachingService>();
        var token = Guid.NewGuid();
        using var bundle = new UserDataBundle();

        cache.SetUserDataBundle(token, bundle);

        MSTestAssert.IsFalse(cache.TryGetUserDataBundle(token, out _));
    }
}
