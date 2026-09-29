using Microsoft.Extensions.DependencyInjection;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using PasswordManagerLocal.Common.Backend.Abstractions.Services;
using PasswordManagerLocal.Common.Backend.Exceptions;
using PasswordManagerLocal.Common.Tests.TestInfrastructure;

using MSTestAssert = Microsoft.VisualStudio.TestTools.UnitTesting.Assert;

namespace PasswordManagerLocal.Common.Tests.Backend.Services;

[TestClass]
public sealed class UserDataReaderServiceTests
{
    [TestMethod]
    public async Task GetLoadAndVerifyUserData_ReturnsIndependentCanonicalBundles()
    {
        using var host = new BackendTestHost();
        var auth = host.Services.GetRequiredService<IAuthService>();
        var reader = host.Services.GetRequiredService<IUserDataReaderService>();
        var cache = host.Services.GetRequiredService<IDataCachingService>();

        var token = await auth.RegisterAsync(host.CreateValidRegistrationRequest("reader_user"));

        using var first = await reader.GetLoadAndVerifyUserDataBundleAsync(token);
        using var second = await reader.GetLoadAndVerifyUserDataBundleAsync(token);

        MSTestAssert.AreNotSame(first, second);
        first.GeneralUserData.FirstName = "Changed working copy";
        MSTestAssert.AreNotEqual(first.GeneralUserData.FirstName, second.GeneralUserData.FirstName);
        MSTestAssert.IsTrue(cache.TryGetUserDataBundle(token, out _));
    }

    [TestMethod]
    public async Task GetLoadAndVerifyUserData_InvalidToken_Throws()
    {
        using var host = new BackendTestHost();
        var reader = host.Services.GetRequiredService<IUserDataReaderService>();

        await MSTestAssert.ThrowsExactlyAsync<InvalidTokenException>(
            () => reader.GetLoadAndVerifyUserDataBundleAsync(Guid.NewGuid()));
    }
}
