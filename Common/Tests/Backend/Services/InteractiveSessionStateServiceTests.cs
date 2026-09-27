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
    public async Task InactiveStateDoesNotResolveInteractiveServices()
    {
        var tokenServiceResolutions = 0;
        var keyVaultResolutions = 0;
        var services = new ServiceCollection();
        services.AddSingleton<ITokenService>(_ =>
        {
            tokenServiceResolutions++;
            return new TokenService();
        });
        services.AddSingleton<IKeyVaultService>(_ =>
        {
            keyVaultResolutions++;
            return new KeyVaultService();
        });
        await using var provider = services.BuildServiceProvider();
        var state = new InteractiveSessionStateService(
            provider.GetRequiredService<IServiceScopeFactory>());

        Assert.IsFalse(state.TryGetUserEncryptionKey(Guid.NewGuid(), out _));
        await state.InvalidateUserCacheAsync(Guid.NewGuid());

        Assert.AreEqual(0, tokenServiceResolutions);
        Assert.AreEqual(0, keyVaultResolutions);
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
    public async Task ActiveStateResolvesInteractiveServicesAndDeactivationReturnsToNoOp()
    {
        var tokenServiceResolutions = 0;
        var keyVaultResolutions = 0;
        var services = new ServiceCollection();
        services.AddSingleton<ITokenService>(_ =>
        {
            tokenServiceResolutions++;
            return new TokenService();
        });
        services.AddSingleton<IKeyVaultService>(_ =>
        {
            keyVaultResolutions++;
            return new KeyVaultService();
        });
        services.AddMemoryCache(options => options.SizeLimit = 10);
        services.AddSingleton<SafeMemoryCache>();
        services.AddSingleton<IDataCachingService, DataCachingService>();
        await using var provider = services.BuildServiceProvider();
        var state = new InteractiveSessionStateService(
            provider.GetRequiredService<IServiceScopeFactory>());

        await state.ActivateAsync();
        Assert.IsFalse(state.TryGetUserEncryptionKey(Guid.NewGuid(), out _));
        Assert.AreEqual(1, tokenServiceResolutions);
        Assert.AreEqual(1, keyVaultResolutions);

        await state.DeactivateAsync();
        Assert.IsFalse(state.TryGetUserEncryptionKey(Guid.NewGuid(), out _));
        Assert.AreEqual(1, tokenServiceResolutions);
        Assert.AreEqual(1, keyVaultResolutions);
    }
}
