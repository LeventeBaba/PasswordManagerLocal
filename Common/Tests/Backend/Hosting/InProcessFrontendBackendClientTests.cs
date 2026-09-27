using Microsoft.Extensions.DependencyInjection;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using PasswordManagerLocal.Common.Contracts.Endpoints;
using PasswordManagerLocal.Common.Backend.Hosting;
using PasswordManagerLocal.Common.Contracts.Runtime;
using PasswordManagerLocal.Common.Contracts.BackgroundSync;
using PasswordManagerLocal.Common.Tests.Fakes;
using PasswordManagerLocal.Common.Tests.TestInfrastructure;

namespace PasswordManagerLocal.Common.Tests.Backend.Hosting;

[TestClass]
public sealed class InProcessFrontendBackendClientTests
{
    [TestMethod]
    public async Task ConnectOpensOneInteractiveSessionAndReturnsItsEndpoints()
    {
        using var host = new BackendTestHost();
        var endpoints = host.Services.GetRequiredService<IEndpoints>();
        var runtime = new FakeBackendRuntime(endpoints);
        var coordinator = new BackendRuntimeLifetimeCoordinator(runtime);
        await using var client = new InProcessFrontendBackendClient(runtime, coordinator);

        await client.ConnectAsync();
        await client.ConnectAsync();

        Assert.AreSame(endpoints, await client.GetEndpointsAsync());
        Assert.AreEqual(1, runtime.OpenInteractiveSessionCalls);
        Assert.AreEqual(BackendLifetimeReason.InteractiveUi, coordinator.ActiveReasons);
    }

    [TestMethod]
    public async Task DisposalClosesSessionAndLeavesBackgroundLeaseRunning()
    {
        using var host = new BackendTestHost();
        var runtime = new FakeBackendRuntime(host.Services.GetRequiredService<IEndpoints>());
        var coordinator = new BackendRuntimeLifetimeCoordinator(runtime);
        var backgroundLease = await coordinator.AcquireAsync(BackendLifetimeReason.BackgroundSync);
        var client = new InProcessFrontendBackendClient(runtime, coordinator);

        await client.ConnectAsync();
        await client.DisposeAsync();

        Assert.AreEqual(1, runtime.ClosedInteractiveSessionCalls);
        Assert.AreEqual(0, runtime.StopCalls);
        Assert.AreEqual(BackendLifetimeReason.BackgroundSync, coordinator.ActiveReasons);

        await backgroundLease.DisposeAsync();
        Assert.AreEqual(1, runtime.StopCalls);
    }

    [TestMethod]
    public async Task CleanupFailureRecoversRuntimeAndPreservesBackgroundOwnership()
    {
        using var host = new BackendTestHost();
        var cleanupFailure = new InvalidOperationException("interactive cleanup failed");
        var runtime = new FakeBackendRuntime(host.Services.GetRequiredService<IEndpoints>())
        {
            InteractiveSessionDisposeFailure = cleanupFailure
        };
        var coordinator = new BackendRuntimeLifetimeCoordinator(runtime);
        await using var backgroundLease = await coordinator.AcquireAsync(
            BackendLifetimeReason.BackgroundSync);
        var client = new InProcessFrontendBackendClient(runtime, coordinator);
        await client.ConnectAsync();
        var ensureStartedCallsBeforeRecovery = runtime.EnsureStartedCalls;

        var thrown = await Assert.ThrowsAsync<InvalidOperationException>(
            async () => await client.DisposeAsync());

        Assert.AreSame(cleanupFailure, thrown);
        Assert.AreEqual(1, runtime.StopCalls);
        Assert.AreEqual(
            ensureStartedCallsBeforeRecovery + 1,
            runtime.EnsureStartedCalls);
        Assert.AreEqual(BackendLifetimeReason.BackgroundSync, coordinator.ActiveReasons);
        Assert.AreEqual(
            InteractiveSessionLifecycleState.None,
            runtime.InteractiveSessionSnapshot.State);

        await client.DisposeAsync();
        Assert.AreEqual(1, runtime.StopCalls);
    }

    [TestMethod]
    public async Task ReadinessAndDatabaseResetAreForwardedAndReconnectInteractiveSession()
    {
        using var host = new BackendTestHost();
        var endpoints = host.Services.GetRequiredService<IEndpoints>();
        var runtime = new FakeBackendRuntime(endpoints);
        var coordinator = new BackendRuntimeLifetimeCoordinator(runtime);
        await using var client = new InProcessFrontendBackendClient(runtime, coordinator);

        await client.ConnectAsync();
        await client.WaitUntilReadyAsync();
        await client.ResetDatabaseAndRestartAsync();

        Assert.IsTrue(runtime.WaitUntilReadyCalls >= 2);
        Assert.AreEqual(1, runtime.ResetCalls);
        Assert.AreEqual(2, runtime.OpenInteractiveSessionCalls);
        Assert.AreSame(endpoints, await client.GetEndpointsAsync());
        Assert.AreEqual(BackendLifetimeReason.InteractiveUi, coordinator.ActiveReasons);
    }

    [TestMethod]
    public async Task FailedInteractiveConnectionReleasesAcquiredRuntimeLease()
    {
        using var host = new BackendTestHost();
        var failure = new InvalidOperationException("session failed");
        var runtime = new FakeBackendRuntime(host.Services.GetRequiredService<IEndpoints>())
        {
            InteractiveSessionFailure = failure
        };
        var coordinator = new BackendRuntimeLifetimeCoordinator(runtime);
        await using var client = new InProcessFrontendBackendClient(runtime, coordinator);

        var thrown = await Assert.ThrowsAsync<InvalidOperationException>(
            async () => await client.ConnectAsync());

        Assert.AreSame(failure, thrown);
        Assert.AreEqual(BackendLifetimeReason.None, coordinator.ActiveReasons);
        Assert.AreEqual(1, runtime.StopCalls);
    }

    [TestMethod]
    public async Task StateChangesAreForwardedUntilDisposal()
    {
        using var host = new BackendTestHost();
        var runtime = new FakeBackendRuntime(host.Services.GetRequiredService<IEndpoints>());
        var coordinator = new BackendRuntimeLifetimeCoordinator(runtime);
        var client = new InProcessFrontendBackendClient(runtime, coordinator);
        var changes = 0;
        client.StateChanged += (_, _) => changes++;

        runtime.SetSnapshot(new BackendRuntimeSnapshot(
            BackendRuntimeState.Starting,
            BackendRuntimeFailureKind.None,
            null,
            DateTimeOffset.UtcNow));
        Assert.AreEqual(1, changes);

        await client.DisposeAsync();
        runtime.SetSnapshot(new BackendRuntimeSnapshot(
            BackendRuntimeState.Ready,
            BackendRuntimeFailureKind.None,
            null,
            DateTimeOffset.UtcNow));
        Assert.AreEqual(1, changes);
    }

    [TestMethod]
    public async Task GetEndpointsBeforeConnectionAndUsageAfterDisposalFailClearly()
    {
        using var host = new BackendTestHost();
        var runtime = new FakeBackendRuntime(host.Services.GetRequiredService<IEndpoints>());
        var coordinator = new BackendRuntimeLifetimeCoordinator(runtime);
        var client = new InProcessFrontendBackendClient(runtime, coordinator);

        await Assert.ThrowsAsync<InvalidOperationException>(
            async () => await client.GetEndpointsAsync());
        await client.DisposeAsync();
        await Assert.ThrowsAsync<ObjectDisposedException>(
            async () => await client.ConnectAsync());
    }
}
