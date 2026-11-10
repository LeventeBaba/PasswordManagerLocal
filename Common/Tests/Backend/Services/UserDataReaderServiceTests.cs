using Microsoft.Extensions.DependencyInjection;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using PasswordManagerLocal.Common.Backend.Abstractions.Services;
using PasswordManagerLocal.Common.Backend.Exceptions;
using PasswordManagerLocal.Common.Tests.TestInfrastructure;
using PasswordManagerLocal.Common.Backend.Abstractions.Repositories;
using PasswordManagerLocal.Common.Backend.Models.Encrypted;

using MSTestAssert = Microsoft.VisualStudio.TestTools.UnitTesting.Assert;

namespace PasswordManagerLocal.Common.Tests.Backend.Services;

[TestClass]
public sealed class UserDataReaderServiceTests
{
    [TestMethod]
    public async Task CachedBundle_FromAnOlderCommittedGeneration_IsNotReturned()
    {
        using var host = new BackendTestHost();
        var services = host.Services;
        var token = await services.GetRequiredService<IAuthService>().RegisterAsync(host.CreateValidRegistrationRequest());
        var reader = services.GetRequiredService<IUserDataReaderService>();
        var cache = services.GetRequiredService<IDataCachingService>();
        using var stale = await reader.GetLoadAndVerifyUserDataBundleAsync(token);
        var writer = services.GetRequiredService<IUserDataWriterService>();
        await writer.ExecuteMutationAsync(token, async ct =>
        {
            using var changed = await reader.GetLoadAndVerifyUserDataBundleAsync(token, ct);
            changed.GeneralUserData.FirstName = "Updated canonical name";
            changed.GeneralUserData.Version = services.GetRequiredService<ISyncVersionClockService>().Next();
            await writer.UpdateUserDataBundleAsync(changed, token, UserDataBlobKind.General, false, ct);
        });
        cache.SetUserDataBundle(token, stale);

        using var current = await reader.GetLoadAndVerifyUserDataBundleAsync(token);

        MSTestAssert.AreEqual("Updated canonical name", current.GeneralUserData.FirstName);
    }

    [TestMethod]
    public async Task ValidCache_DoesNotBypassCanonicalRowIntegrity()
    {
        using var host = new BackendTestHost();
        var services = host.Services;
        var token = await services.GetRequiredService<IAuthService>().RegisterAsync(host.CreateValidRegistrationRequest());
        var cache = services.GetRequiredService<IDataCachingService>();
        MSTestAssert.IsTrue(cache.TryGetUserDataBundle(token, out var cached));
        using var cachedBundle = cached;
        MSTestAssert.IsNotNull(cachedBundle);

        var userId = services.GetRequiredService<ITokenService>().GetUidOrThrow(token);
        var user = await services.GetRequiredService<IUserRepository>().GetByIdAsync(userId);
        user!.EncryptedPayload[0] ^= 0x01;
        services.GetRequiredService<IUserRepository>().Update(user);

        await MSTestAssert.ThrowsExactlyAsync<InvalidDataIntegrityException>(() =>
            services.GetRequiredService<IUserDataReaderService>().GetLoadAndVerifyUserDataBundleAsync(token));
    }

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
