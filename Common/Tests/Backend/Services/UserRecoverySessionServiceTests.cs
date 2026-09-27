using Microsoft.VisualStudio.TestTools.UnitTesting;
using PasswordManagerLocal.Common.Backend.Abstractions.Repositories;
using PasswordManagerLocal.Common.Backend.Abstractions.Services;
using PasswordManagerLocal.Common.Backend.Models;
using PasswordManagerLocal.Common.Tests.TestInfrastructure;

using MSTestAssert = Microsoft.VisualStudio.TestTools.UnitTesting.Assert;

namespace PasswordManagerLocal.Common.Tests.Backend.Services;

[TestClass]
public sealed class UserRecoverySessionServiceTests
{
    [TestMethod]
    [TestCategory("Backend")]
    [TestCategory("Recovery")]
    [TestCategory("Session")]
    public async Task RefreshOrInvalidateAsync_RevokesSupersededGenerationAndClearsProcessState()
    {
        using var host = new BackendTestHost();
        var auth = host.Services.GetRequiredService<IAuthService>();
        var token = await auth.RegisterAsync(host.CreateValidRegistrationRequest());
        var tokens = host.Services.GetRequiredService<ITokenService>();
        var userId = tokens.GetUidOrThrow(token);
        var user = await host.Services.GetRequiredService<IUserRepository>().GetByIdAsync(userId);
        MSTestAssert.IsNotNull(user);
        MSTestAssert.IsTrue(host.Services.GetRequiredService<IDataCachingService>()
            .TryGetUserDataBundle(token, out _));
        MSTestAssert.IsTrue(host.Services.GetRequiredService<IKeyVaultService>().HasUserKey(token));

        await host.Services.GetRequiredService<IUserRecoverySessionService>()
            .RefreshOrInvalidateAsync(user);

        MSTestAssert.IsFalse(tokens.Validate(token));
        MSTestAssert.IsTrue(tokens.TryGetInvalidationReason(
            token,
            out var reason));
        MSTestAssert.AreEqual(AuthSessionInvalidationReason.CanonicalRecovered, reason);
        MSTestAssert.IsFalse(host.Services.GetRequiredService<IDataCachingService>()
            .TryGetUserDataBundle(token, out _));
        MSTestAssert.IsFalse(host.Services.GetRequiredService<IKeyVaultService>().HasUserKey(token));
    }
}
