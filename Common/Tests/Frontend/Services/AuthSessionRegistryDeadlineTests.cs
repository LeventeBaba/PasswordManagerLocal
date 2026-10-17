using Microsoft.VisualStudio.TestTools.UnitTesting;
using PasswordManagerLocal.Common.Frontend.Services;

namespace PasswordManagerLocal.Common.Tests.Frontend.Services;

[TestClass]
public sealed class AuthSessionRegistryDeadlineTests
{
    [TestMethod]
    public void RememberMeAndProfileRefreshPreserveExpiry_WhileTokenReplacementClearsIt()
    {
        using var registry = new AuthSessionRegistry();
        var token = Guid.NewGuid();
        var userId = Guid.NewGuid();
        var expiry = DateTimeOffset.Parse("2030-01-01T12:00:00Z");
        Assert.IsTrue(registry.TryAdd(token));
        Assert.IsTrue(registry.TrySetProfile(token, userId, "Profile", "Subtitle", "user", "user@example.test", true));
        Assert.IsTrue(registry.TrySetExpiresAtUtc(token, expiry));

        Assert.IsTrue(registry.TrySetRememberMe(token, false));
        Assert.IsFalse(registry.GetSession(token)?.IsRememberMeEnabled);
        Assert.AreEqual(expiry, registry.GetSession(token)?.ExpiresAtUtc);
        Assert.IsTrue(registry.TrySetRememberMe(token, true));
        Assert.IsTrue(registry.GetSession(token)?.IsRememberMeEnabled);
        Assert.AreEqual(expiry, registry.GetSession(token)?.ExpiresAtUtc);
        Assert.IsTrue(registry.TrySetProfile(token, userId, "Updated", "Updated", "user", "user@example.test", true));
        Assert.AreEqual(expiry, registry.GetSession(token)?.ExpiresAtUtc);

        var renewedToken = Guid.NewGuid();
        Assert.IsTrue(registry.TryReplaceToken(token, renewedToken));
        var renewed = registry.GetSession(renewedToken);
        Assert.IsNotNull(renewed);
        Assert.AreEqual(userId, renewed.UserId);
        Assert.IsTrue(renewed.IsRememberMeEnabled);
        Assert.IsNull(renewed.ExpiresAtUtc);
    }
}
