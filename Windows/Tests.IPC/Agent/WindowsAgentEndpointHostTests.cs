using Microsoft.VisualStudio.TestTools.UnitTesting;
using PasswordManagerLocal.Common.Contracts.Endpoints;
using PasswordManagerLocal.Common.Contracts.Runtime;
using PasswordManagerLocal.Common.Contracts.BackgroundSync;
using PasswordManagerLocal.Windows.Agent.Backend;
using PasswordManagerLocal.Windows.Agent.Endpoint;
using PasswordManagerLocal.Windows.Agent.Hosting;
using PasswordManagerLocal.Windows.Ipc.Lifecycle;
using PasswordManagerLocal.Windows.Ipc.Protocol;
using PasswordManagerLocal.Windows.Tests.IPC.Infrastructure;
using System.Reflection;

namespace PasswordManagerLocal.Windows.Tests.IPC.Agent;

[TestClass]
public sealed class WindowsAgentEndpointHostTests
{
    [TestMethod]
    public async Task StartsInjectedListenerAndPublishesReady()
    {
        var context = CreateContext();
        await using var host = context.Host;

        await host.StartAsync();

        Assert.AreEqual(1, context.ServerHost.StartCount);
        Assert.AreEqual(WindowsAgentEndpointHostState.Ready, host.Snapshot.State);
        Assert.IsNull(host.Snapshot.Failure);
    }

    [TestMethod]
    public async Task ListenerStartupFailurePublishesFailedAndCleansFactories()
    {
        var failure = new IOException("endpoint listener start failed");
        var context = CreateContext();
        context.ServerHost.StartFailure = failure;
        await using var host = context.Host;

        var observed = await Assert.ThrowsExactlyAsync<IOException>(() => host.StartAsync());

        Assert.AreSame(failure, observed);
        Assert.AreEqual(WindowsAgentEndpointHostState.Failed, host.Snapshot.State);
        Assert.AreSame(failure, host.Snapshot.Failure);
        Assert.AreEqual(1, context.ServerHost.DisposeCount);
        Assert.AreEqual(1, context.SessionFactory.DisposeCount);
    }

    [TestMethod]
    public async Task StartupCleanupFailureIsAggregatedAndFailedSnapshotIsPreserved()
    {
        var startFailure = new IOException("endpoint listener start failed");
        var cleanupFailure = new IOException("endpoint listener cleanup failed");
        var context = CreateContext();
        context.ServerHost.StartFailure = startFailure;
        context.ServerHost.DisposeFailure = cleanupFailure;
        var host = context.Host;

        var observed = await Assert.ThrowsExactlyAsync<AggregateException>(
            () => host.StartAsync());

        Assert.AreEqual(2, observed.Flatten().InnerExceptions.Count);
        Assert.AreEqual(WindowsAgentEndpointHostState.Failed, host.Snapshot.State);
        Assert.IsInstanceOfType<AggregateException>(host.Snapshot.Failure);
        await host.DisposeAsync();
        Assert.AreEqual(WindowsAgentEndpointHostState.Failed, host.Snapshot.State);
    }

    [TestMethod]
    public async Task ListenerFailureCannotLeaveFalseReadyState()
    {
        var context = CreateContext();
        await using var host = context.Host;
        await host.StartAsync();
        var failure = new IOException("endpoint listener failed");

        context.ServerHost.FailListener(failure);
        await WaitUntilAsync(() => host.Snapshot.State == WindowsAgentEndpointHostState.Failed);

        Assert.AreEqual(WindowsAgentEndpointHostState.Failed, host.Snapshot.State);
        Assert.AreSame(failure, host.Snapshot.Failure);
    }

    [TestMethod]
    public async Task StopClosesInteractiveSessionBeforeFactoryTransferStateIsDisposed()
    {
        var operations = new List<string>();
        var drainRelease = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var endpoints = DispatchProxy.Create<IEndpoints, TestEndpointsDispatchProxy>();
        var session = new FakeAgentInteractiveBackendSession(endpoints, operations, drainRelease)
        {
            ActiveOperationCount = 1
        };
        var lease = new FakeAgentBackendRuntimeLease(
            BackendLifetimeReason.InteractiveUi,
            operations);
        var owner = new FakeWindowsAgentBackendRuntimeOwner
        {
            InteractiveBindingFactory = _ => Task.FromResult(
                new AgentInteractiveBackendBinding(
                    session,
                    lease,
                    () => ValueTask.CompletedTask))
        };
        var context = CreateContext(owner);
        context.ServerHost.OperationLog = operations;
        context.SessionFactory.ActiveLargeResultTransferCount = 2;
        await using var host = context.Host;
        await host.StartAsync();
        operations.Clear();
        var connection = CreateConnection(Guid.NewGuid(), Guid.NewGuid());
        await context.Adapter.OnConnectionLifecycleChangedAsync(
            new IpcConnectionLifecycleNotification(
                connection.ConnectionId,
                IpcConnectionLifecycleState.HandshakeCompleted,
                connection,
                IpcDisconnectKind.None,
                DateTimeOffset.UtcNow));

        var stopping = host.StopAsync();
        await WaitUntilAsync(() => operations.Contains("session-close-admission"));

        Assert.IsFalse(stopping.IsCompleted);
        Assert.IsFalse(context.Adapter.AcceptsNewOperations);
        Assert.AreEqual(1, context.ServerHost.StopCount);
        Assert.AreEqual(0, context.SessionFactory.DisposeCount);
        drainRelease.TrySetResult();
        await stopping;

        CollectionAssert.AreEqual(
            new[]
            {
                "control-stop",
                "control-dispose",
                "session-close-admission",
                "session-dispose",
                "lease-dispose"
            },
            operations);
        Assert.AreEqual(1, session.DisposeCount);
        Assert.AreEqual(1, lease.DisposeCount);
        Assert.AreEqual(1, context.SessionFactory.DisposeCount);
        Assert.AreEqual(0, context.SessionFactory.ActiveLargeResultTransferCount);
        Assert.AreEqual(WindowsAgentEndpointHostState.Stopped, host.Snapshot.State);
    }

    [TestMethod]
    public async Task RegistrationReplacementClosesActiveEndpointSessions()
    {
        var coordinator = new SingleUiConnectionCoordinator();
        var instanceId = Guid.NewGuid();
        Assert.IsTrue(coordinator.TryRegister(
            CreateConnection(Guid.NewGuid(), instanceId),
            out _));
        var context = CreateContext(coordinator: coordinator);
        context.ServerHost.ActiveSessionCount = 1;
        await using var host = context.Host;
        await host.StartAsync();

        Assert.IsTrue(coordinator.TryRegister(
            CreateConnection(Guid.NewGuid(), instanceId),
            out _));
        await WaitUntilAsync(() => context.ServerHost.CloseActiveSessionsCount == 1);

        Assert.AreEqual(0, context.ServerHost.ActiveSessionCount);
        Assert.AreEqual(WindowsAgentEndpointHostState.Ready, host.Snapshot.State);
    }

    [TestMethod]
    public async Task StopFailureIsTruthfullyPublishedAndFactoryCleanupStillRuns()
    {
        var failure = new IOException("endpoint stop failed");
        var context = CreateContext();
        context.ServerHost.StopFailure = failure;
        await using var host = context.Host;
        await host.StartAsync();

        var observed = await Assert.ThrowsExactlyAsync<IOException>(() => host.StopAsync());

        Assert.AreSame(failure, observed);
        Assert.AreEqual(WindowsAgentEndpointHostState.Failed, host.Snapshot.State);
        Assert.AreEqual(1, context.ServerHost.DisposeCount);
        Assert.AreEqual(1, context.SessionFactory.DisposeCount);
    }

    [TestMethod]
    public void ProductionConnectionBoundIsOneAuthoritativeEndpoint()
    {
        Assert.AreEqual(1, WindowsAgentEndpointHost.MaximumActiveEndpointConnections);
    }

    private static EndpointHostTestContext CreateContext(
        FakeWindowsAgentBackendRuntimeOwner? owner = null,
        SingleUiConnectionCoordinator? coordinator = null)
    {
        owner ??= new FakeWindowsAgentBackendRuntimeOwner();
        coordinator ??= new SingleUiConnectionCoordinator();
        var state = new WindowsAgentStateStore();
        state.MarkRunning(DateTimeOffset.UtcNow);
        var admissionGate = new WindowsAgentAdmissionGate();
        admissionGate.Open();
        var adapter = new AgentInteractiveEndpointAdapter(owner);
        var resolver = new RegisteredUiEndpointRegistrationResolver(
            coordinator,
            admissionGate);
        var admissionPolicy = new WindowsAgentEndpointAdmissionPolicy(
            admissionGate,
            state,
            owner,
            () => WindowsAgentEndpointHostState.Ready);
        var sessionFactory = new FakeEndpointRpcServerSessionFactory();
        var serverHost = new FakeWindowsIpcServerHost();
        var host = new WindowsAgentEndpointHost(
            "test-endpoint-pipe",
            adapter,
            resolver,
            admissionPolicy,
            (_, _, _) => sessionFactory,
            (_, _) => serverHost);
        return new EndpointHostTestContext(
            host,
            adapter,
            sessionFactory,
            serverHost);
    }

    private static IpcConnectionContext CreateConnection(
        Guid connectionId,
        Guid instanceId) => new(
            connectionId,
            IpcPeerRole.Ui,
            PeerProcessId: 100,
            PeerWindowsSessionId: 2,
            PeerSessionId: instanceId,
            PeerCapabilities: IpcCapabilities.EndpointRpc);

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        while (!condition())
            await Task.Delay(10, timeout.Token);
    }

    private sealed record EndpointHostTestContext(
        WindowsAgentEndpointHost Host,
        AgentInteractiveEndpointAdapter Adapter,
        FakeEndpointRpcServerSessionFactory SessionFactory,
        FakeWindowsIpcServerHost ServerHost);
}
