using Microsoft.Extensions.DependencyInjection;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using PasswordManagerLocal.Common.Backend.Abstractions.Services;
using PasswordManagerLocal.Common.Backend.Models;
using PasswordManagerLocal.Common.Backend.Security;
using PasswordManagerLocal.Common.Backend.Services;
using PasswordManagerLocal.Common.Tests.TestInfrastructure;

namespace PasswordManagerLocal.Common.Tests.Backend.Services;

[TestClass]
public sealed class UserSyncKeyResolverServiceTests
{
    [TestMethod]
    public async Task SessionKeyIsUnavailableAfterClosingButSavedProtectedKeyRemainsSeparate()
    {
        var services = new ServiceCollection();
        services.AddSingleton<ITokenService, TokenService>();
        services.AddSingleton<IKeyVaultService, KeyVaultService>();
        await using var provider = services.BuildServiceProvider();
        var state = new InteractiveSessionStateService(
            provider.GetRequiredService<IServiceScopeFactory>());
        var protector = new TestKeyProtector();
        var resolver = new UserSyncKeyResolverService(state, protector);
        var userId = Guid.NewGuid();
        var sessionRaw = Enumerable.Repeat((byte)3, 32).ToArray();
        var savedRaw = Enumerable.Repeat((byte)7, 32).ToArray();
        var user = new User
        {
            UId = userId,
            SavedKey = protector.Protect(savedRaw)
        };
        var token = provider.GetRequiredService<ITokenService>().Issue(userId);
        using var sessionKey = EncryptionKey.FromRaw(sessionRaw);
        provider.GetRequiredService<IKeyVaultService>().SetUserKey(
            token,
            sessionKey,
            DateTimeOffset.UtcNow.AddMinutes(5));

        await state.ActivateAsync();
        Assert.IsTrue(resolver.TryResolve(user, out var activeKey, out var activeConfidence));
        Assert.AreEqual(UserSyncKeyConfidence.AuthenticatedSession, activeConfidence);
        using (activeKey)
            CollectionAssert.AreEqual(sessionRaw, activeKey!.AsSpan().ToArray());

        await state.DeactivateAsync();
        Assert.IsTrue(resolver.TryResolve(user, out var backgroundKey, out var backgroundConfidence));
        Assert.AreEqual(UserSyncKeyConfidence.RememberMe, backgroundConfidence);
        using (backgroundKey)
            CollectionAssert.AreEqual(savedRaw, backgroundKey!.AsSpan().ToArray());
        Assert.IsNotNull(protector.LastUnprotectedBuffer);
        Assert.IsTrue(protector.LastUnprotectedBuffer!.All(value => value == 0));
    }

    [TestMethod]
    public async Task ClosingRejectsNewSessionKeyResolutionWithoutRemovingSavedKeyCapability()
    {
        var services = new ServiceCollection();
        services.AddSingleton<ITokenService, TokenService>();
        services.AddSingleton<IKeyVaultService, KeyVaultService>();
        await using var provider = services.BuildServiceProvider();
        var state = new InteractiveSessionStateService(
            provider.GetRequiredService<IServiceScopeFactory>());
        var protector = new TestKeyProtector();
        var resolver = new UserSyncKeyResolverService(state, protector);
        var savedRaw = Enumerable.Repeat((byte)11, 32).ToArray();
        var user = new User
        {
            UId = Guid.NewGuid(),
            SavedKey = protector.Protect(savedRaw)
        };

        await state.ActivateAsync();
        await state.DeactivateAsync();

        Assert.IsFalse(state.TryGetUserEncryptionKey(user.UId, out _));
        Assert.IsTrue(resolver.TryResolve(user, out var key, out var confidence));
        Assert.AreEqual(UserSyncKeyConfidence.RememberMe, confidence);
        Assert.IsNotNull(protector.LastUnprotectedBuffer);
        Assert.IsTrue(protector.LastUnprotectedBuffer!.All(value => value == 0));
        key!.Dispose();
        Assert.Throws<ObjectDisposedException>(() => key.AsSpan());
    }
}
