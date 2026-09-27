using Microsoft.Extensions.DependencyInjection;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using PasswordManagerLocal.Common.Contracts.Endpoints;
using PasswordManagerLocal.Common.Backend.Abstractions.Services;
using PasswordManagerLocal.Common.Backend.Hosting;
using PasswordManagerLocal.Common.Backend.Models.Encrypted;
using PasswordManagerLocal.Common.Backend.Security;
using PasswordManagerLocal.Common.Backend.Services;
using PasswordManagerLocal.Common.Tests.TestInfrastructure;

namespace PasswordManagerLocal.Common.Tests.Backend.Hosting;

[TestClass]
public sealed class InteractiveBackendSessionTests
{
    [TestMethod]
    public async Task ClosingStartedCallbackRunsBeforeInteractiveStateDeactivation()
    {
        using var host = new BackendTestHost();
        var state = new InteractiveSessionStateService(
            host.Services.GetRequiredService<IServiceScopeFactory>());
        await state.ActivateAsync();
        var callbackSawActiveState = false;
        var session = new InteractiveBackendSession(
            host.Services.GetRequiredService<IEndpoints>(),
            state,
            _ => ValueTask.CompletedTask);

        await session.BeginCloseAsync(() => callbackSawActiveState = state.IsActive);

        Assert.IsTrue(callbackSawActiveState);
        Assert.IsFalse(state.IsActive);
    }

    [TestMethod]
    public async Task ClosingRejectsNewOperationsAndWaitsForAdmittedOperation()
    {
        using var host = new BackendTestHost();
        var state = new InteractiveSessionStateService(
            host.Services.GetRequiredService<IServiceScopeFactory>());
        await state.ActivateAsync();
        var operationStarted = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var continueOperation = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseEntered = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);

        async ValueTask ReleaseAsync(InteractiveBackendSession closingSession)
        {
            releaseEntered.TrySetResult();
            await closingSession.BeginCloseAsync();
        }

        var session = new InteractiveBackendSession(
            host.Services.GetRequiredService<IEndpoints>(),
            state,
            ReleaseAsync);
        var operation = session.ExecuteAsync(async _ =>
        {
            operationStarted.TrySetResult();
            await continueOperation.Task;
            Assert.IsTrue(state.ExecuteRequired(() => true));
        });

        await operationStarted.Task;
        var disposal = session.DisposeAsync().AsTask();
        await releaseEntered.Task;

        Assert.IsFalse(disposal.IsCompleted);
        Assert.IsFalse(state.IsActive);
        await Assert.ThrowsAsync<ObjectDisposedException>(
            async () => await session.ExecuteAsync(_ => Task.CompletedTask));

        continueOperation.TrySetResult();
        await operation;
        await disposal;
    }

    [TestMethod]
    public async Task ClosingWaitsForAllAdmittedOperations()
    {
        using var host = new BackendTestHost();
        var state = new InteractiveSessionStateService(
            host.Services.GetRequiredService<IServiceScopeFactory>());
        await state.ActivateAsync();
        var startedCount = 0;
        var allStarted = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseOperations = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);

        async ValueTask ReleaseAsync(InteractiveBackendSession closingSession)
        {
            await closingSession.BeginCloseAsync();
        }

        var session = new InteractiveBackendSession(
            host.Services.GetRequiredService<IEndpoints>(),
            state,
            ReleaseAsync);
        var operations = Enumerable.Range(0, 3)
            .Select(index => session.ExecuteAsync(async endpoints =>
            {
                if (Interlocked.Increment(ref startedCount) == 3)
                    allStarted.TrySetResult();
                await releaseOperations.Task;
            }))
            .ToArray();

        await allStarted.Task;
        var disposal = session.DisposeAsync().AsTask();
        Assert.IsFalse(disposal.IsCompleted);

        releaseOperations.TrySetResult();
        await Task.WhenAll(operations);
        await disposal;
    }

    [TestMethod]
    public async Task OperationExceptionStillAllowsSessionToDrain()
    {
        using var host = new BackendTestHost();
        var state = new InteractiveSessionStateService(
            host.Services.GetRequiredService<IServiceScopeFactory>());
        await state.ActivateAsync();

        async ValueTask ReleaseAsync(InteractiveBackendSession closingSession)
        {
            await closingSession.BeginCloseAsync();
        }

        var session = new InteractiveBackendSession(
            host.Services.GetRequiredService<IEndpoints>(),
            state,
            ReleaseAsync);

        await Assert.ThrowsAsync<InvalidOperationException>(
            async () => await session.ExecuteAsync(
                _ => Task.FromException(new InvalidOperationException("operation failed"))));
        await session.DisposeAsync();
        Assert.IsFalse(state.IsActive);
    }

    [TestMethod]
    public async Task AdmittedOperationKeepsSessionKeyCopyWhileNewAcquisitionIsRejected()
    {
        using var host = new BackendTestHost();
        var state = new InteractiveSessionStateService(
            host.Services.GetRequiredService<IServiceScopeFactory>());
        var tokens = host.Services.GetRequiredService<ITokenService>();
        var keys = host.Services.GetRequiredService<IKeyVaultService>();
        var userId = Guid.NewGuid();
        var token = tokens.Issue(userId);
        using var sharedKey = EncryptionKey.FromRaw(Enumerable.Repeat((byte)9, 32).ToArray());
        keys.SetUserKey(token, sharedKey, DateTimeOffset.UtcNow.AddMinutes(5));
        await state.ActivateAsync();

        var operationStarted = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var continueOperation = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        EncryptionKey? operationKey = null;

        async ValueTask ReleaseAsync(InteractiveBackendSession closingSession)
        {
            await closingSession.BeginCloseAsync();
            keys.ClearAll();
            tokens.RevokeAll();
        }

        var session = new InteractiveBackendSession(
            host.Services.GetRequiredService<IEndpoints>(),
            state,
            ReleaseAsync);
        var operation = session.ExecuteAsync(async _ =>
        {
            operationStarted.TrySetResult();
            await continueOperation.Task;
            Assert.IsTrue(tokens.Validate(token));
            Assert.IsTrue(state.TryGetUserEncryptionKey(userId, out operationKey));
            Assert.IsNotNull(operationKey);
            using var keyCopy = operationKey!;
            Assert.AreEqual(32, keyCopy.AsSpan().Length);
        });

        await operationStarted.Task;
        var disposal = session.DisposeAsync().AsTask();
        await WaitUntilAsync(() => !state.IsActive);
        Assert.IsFalse(state.TryGetUserEncryptionKey(userId, out _));

        continueOperation.TrySetResult();
        await operation;
        Assert.IsNotNull(operationKey);
        Assert.Throws<ObjectDisposedException>(() => operationKey.AsSpan());
        await disposal;
        Assert.IsFalse(keys.HasUserKey(token));
        Assert.IsFalse(tokens.Validate(token));
    }

    [TestMethod]
    public async Task CacheAndTokenStateRemainForAdmittedWorkThenClearAfterDrain()
    {
        using var host = new BackendTestHost();
        var state = new InteractiveSessionStateService(
            host.Services.GetRequiredService<IServiceScopeFactory>());
        var tokens = host.Services.GetRequiredService<ITokenService>();
        var keys = host.Services.GetRequiredService<IKeyVaultService>();
        var cache = host.Services.GetRequiredService<IDataCachingService>();
        var memoryCache = host.Services.GetRequiredService<SafeMemoryCache>();
        var resetter = new InteractiveSensitiveStateResetter(tokens, cache, keys, memoryCache);
        var token = tokens.Issue(Guid.NewGuid());
        cache.SetUserData(token, CreateUserData());
        await state.ActivateAsync();
        var operationStarted = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var continueOperation = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var newPopulationStarted = false;

        async ValueTask ReleaseAsync(InteractiveBackendSession closingSession)
        {
            await closingSession.BeginCloseAsync();
            await resetter.ResetAsync();
        }

        var session = new InteractiveBackendSession(
            host.Services.GetRequiredService<IEndpoints>(),
            state,
            ReleaseAsync);
        var operation = session.ExecuteAsync(async _ =>
        {
            operationStarted.TrySetResult();
            await continueOperation.Task;
            Assert.IsTrue(state.ExecuteRequired(() => tokens.Validate(token)));
            Assert.IsTrue(state.ExecuteRequired(() =>
                cache.TryGetUserData(token, out var cached) && cached is not null));
        });

        await operationStarted.Task;
        var disposal = session.DisposeAsync().AsTask();
        await WaitUntilAsync(() => !state.IsActive);
        await Assert.ThrowsAsync<ObjectDisposedException>(async () =>
            await session.ExecuteAsync(_ =>
            {
                newPopulationStarted = true;
                return Task.CompletedTask;
            }));
        Assert.IsFalse(newPopulationStarted);

        continueOperation.TrySetResult();
        await operation;
        await disposal;
        Assert.IsFalse(tokens.Validate(token));
        Assert.IsFalse(cache.TryGetUserData(token, out _));
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

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        var timeoutAt = DateTimeOffset.UtcNow.AddSeconds(3);
        while (!condition())
        {
            if (DateTimeOffset.UtcNow >= timeoutAt)
                Assert.Fail("The expected interactive state was not reached.");

            await Task.Delay(10);
        }
    }

}
