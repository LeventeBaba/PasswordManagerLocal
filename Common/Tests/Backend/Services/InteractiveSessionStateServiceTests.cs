using Microsoft.Extensions.DependencyInjection;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using PasswordManagerLocal.Common.Backend.Abstractions.Services;
using PasswordManagerLocal.Common.Backend.Security;
using PasswordManagerLocal.Common.Backend.Services;

namespace PasswordManagerLocal.Common.Tests.Backend.Services;

[TestClass]
public sealed class InteractiveSessionStateServiceTests
{
    [TestMethod]
    public async Task DeactivationCancelsDeferredWorkLifetimeAndReactivationUsesANewLifetime()
    {
        using var provider = new ServiceCollection().BuildServiceProvider();
        using var state = new InteractiveSessionStateService(provider.GetRequiredService<IServiceScopeFactory>());
        Assert.IsTrue(state.CurrentLifetime.IsCancellationRequested);
        await state.ActivateAsync();
        var first = state.CurrentLifetime;
        Assert.IsFalse(first.IsCancellationRequested);

        await state.DeactivateAsync();

        Assert.IsTrue(first.IsCancellationRequested);
        await state.ActivateAsync();
        Assert.IsFalse(state.CurrentLifetime.IsCancellationRequested);
        Assert.AreNotEqual(first, state.CurrentLifetime);
        await state.DeactivateAsync();
    }

    [TestMethod]
    public void InteractiveUserDataAccessorRejectsInactiveStateBeforeResolvingSensitiveServices()
    {
        var userSessionResolutions = 0;
        var services = new ServiceCollection();
        services.AddSingleton<IUserSessionService>(_ =>
        {
            userSessionResolutions++;
            throw new InvalidOperationException("The user-session service must not be resolved.");
        });
        using var provider = services.BuildServiceProvider();
        var state = new InteractiveSessionStateService(
            provider.GetRequiredService<IServiceScopeFactory>());
        var accessor = new InteractiveUserDataStateAccessor(
            provider.GetRequiredService<IServiceScopeFactory>(),
            state);

        Assert.Throws<InvalidOperationException>(() => accessor.GetUserIdFromToken(Guid.NewGuid()));
        Assert.AreEqual(0, userSessionResolutions);
    }

    [TestMethod]
    public async Task DeactivationWaitsForRequiredInteractiveOperationsToDrain()
    {
        var services = new ServiceCollection();
        await using var provider = services.BuildServiceProvider();
        var state = new InteractiveSessionStateService(
            provider.GetRequiredService<IServiceScopeFactory>());
        var operationStarted = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseOperation = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);

        await state.ActivateAsync();
        var operation = Task.Run(() => state.ExecuteRequired(() =>
        {
            operationStarted.TrySetResult();
            releaseOperation.Task.GetAwaiter().GetResult();
            return true;
        }));

        await operationStarted.Task;
        var deactivation = state.DeactivateAsync();
        Assert.IsFalse(deactivation.IsCompleted);

        releaseOperation.TrySetResult();
        await operation;
        await deactivation;
        Assert.IsFalse(state.IsActive);
        Assert.Throws<InvalidOperationException>(() => state.ExecuteRequired(() => true));
    }

    [TestMethod]
    public async Task AdmittedOperationRetainsAccessAfterDeactivationStarts()
    {
        var services = new ServiceCollection();
        await using var provider = services.BuildServiceProvider();
        var state = new InteractiveSessionStateService(
            provider.GetRequiredService<IServiceScopeFactory>());
        await state.ActivateAsync();
        var lease = state.EnterOperation();

        var deactivation = state.DeactivateAsync();

        Assert.IsFalse(state.IsActive);
        Assert.IsFalse(deactivation.IsCompleted);
        Assert.IsTrue(state.ExecuteRequired(() => true));
        Assert.Throws<InvalidOperationException>(() => state.EnterOperation());

        lease.Dispose();
        await deactivation;
    }


    [TestMethod]
    public async Task CapturedOperationContextCannotAuthorizeWorkAfterItsLeaseCompletes()
    {
        var services = new ServiceCollection();
        await using var provider = services.BuildServiceProvider();
        var state = new InteractiveSessionStateService(
            provider.GetRequiredService<IServiceScopeFactory>());
        await state.ActivateAsync();
        var releaseCapturedWork = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var lease = state.EnterOperation();
        var capturedWork = Task.Run(async () =>
        {
            await releaseCapturedWork.Task;
            Assert.Throws<InvalidOperationException>(() => state.ExecuteRequired(() => true));
        });

        lease.Dispose();
        await state.DeactivateAsync();
        releaseCapturedWork.TrySetResult();
        await capturedWork;
    }

    [TestMethod]
    public async Task OnlyActiveStateCanResolveServicesAndReadAnExistingSessionKey()
    {
        var tokenServiceResolutions = 0;
        var keyVaultResolutions = 0;
        var userId = Guid.NewGuid();
        var tokens = new TokenService();
        var token = tokens.Issue(userId);
        var keyVault = new KeyVaultService();
        using var key = EncryptionKey.Create();
        keyVault.SetUserKey(token, key);

        var services = new ServiceCollection();
        services.AddScoped<ITokenService>(_ =>
        {
            tokenServiceResolutions++;
            return tokens;
        });
        services.AddScoped<IKeyVaultService>(_ =>
        {
            keyVaultResolutions++;
            return keyVault;
        });
        await using var provider = services.BuildServiceProvider();
        using var state = new InteractiveSessionStateService(provider.GetRequiredService<IServiceScopeFactory>());

        try
        {
            Assert.IsFalse(state.TryGetUserEncryptionKey(userId, out _));
            await state.InvalidateUserCacheAsync(userId);
            Assert.AreEqual(0, tokenServiceResolutions);
            Assert.AreEqual(0, keyVaultResolutions);

            await state.ActivateAsync();
            Assert.IsTrue(state.TryGetUserEncryptionKey(userId, out var resolved));
            using (resolved)
            {
                Assert.IsNotNull(resolved);
                Assert.IsTrue(key.AsSpan().SequenceEqual(resolved.AsSpan()));
            }
            Assert.AreEqual(1, tokenServiceResolutions);
            Assert.AreEqual(1, keyVaultResolutions);

            await state.DeactivateAsync();
            Assert.IsFalse(state.TryGetUserEncryptionKey(userId, out _));
            await state.InvalidateUserCacheAsync(userId);
            Assert.AreEqual(1, tokenServiceResolutions);
            Assert.AreEqual(1, keyVaultResolutions);
        }
        finally
        {
            keyVault.InvalidateToken(token);
        }
    }
}
