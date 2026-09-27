using Microsoft.VisualStudio.TestTools.UnitTesting;
using PasswordManagerLocal.Common.Contracts.Endpoints;
using PasswordManagerLocal.Common.Contracts.Runtime;
using PasswordManagerLocal.Common.Contracts.BackgroundSync;
using PasswordManagerLocal.Windows.Agent.Backend;
using PasswordManagerLocal.Windows.Agent.Endpoint;
using PasswordManagerLocal.Windows.EndpointRpc.Server;
using PasswordManagerLocal.Windows.Ipc.Lifecycle;
using PasswordManagerLocal.Windows.Ipc.Protocol;
using PasswordManagerLocal.Windows.Tests.IPC.Infrastructure;
using System.Reflection;

namespace PasswordManagerLocal.Windows.Tests.IPC.Agent;

[TestClass]
public sealed class AgentInteractiveEndpointAdapterTests
{
    [TestMethod]
    public async Task AuthorizedConnectionOpensOneSessionBoundEndpointBinding()
    {
        var endpoints = DispatchProxy.Create<IEndpoints, TestEndpointsDispatchProxy>();
        var session = new FakeAgentInteractiveBackendSession(endpoints);
        var lease = new FakeAgentBackendRuntimeLease(BackendLifetimeReason.InteractiveUi);
        var owner = new FakeWindowsAgentBackendRuntimeOwner
        {
            InteractiveBindingFactory = _ => Task.FromResult(
                new AgentInteractiveBackendBinding(session, lease, () => ValueTask.CompletedTask))
        };
        await using var adapter = new AgentInteractiveEndpointAdapter(owner);
        var connection = CreateConnection();

        await adapter.OnConnectionLifecycleChangedAsync(new IpcConnectionLifecycleNotification(
            connection.ConnectionId,
            IpcConnectionLifecycleState.HandshakeCompleted,
            connection,
            IpcDisconnectKind.None,
            DateTimeOffset.UtcNow));

        Assert.AreEqual(1, owner.OpenBindingCount);
        Assert.IsTrue(adapter.IsReady(connection));
        Assert.AreSame(endpoints, adapter.GetEndpoints(new EndpointRequestContext(
            connection.ConnectionId,
            CorrelationId: 1,
            OperationId: PasswordManagerLocal.Windows.EndpointRpc.Contracts.EndpointOperationId.GetLocalDeviceInfo,
            PeerRole: IpcPeerRole.Ui,
            PeerProcessId: connection.PeerProcessId,
            PeerSessionId: connection.PeerSessionId,
            CancellationToken.None)));
        Assert.IsTrue(adapter.AcceptsNewOperations);
    }

    [TestMethod]
    public async Task DisconnectClosesAdmissionDrainsSessionThenReleasesLease()
    {
        var operations = new List<string>();
        var drainRelease = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
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
                    () =>
                    {
                        operations.Add("binding-release");
                        return ValueTask.CompletedTask;
                    }))
        };
        await using var adapter = new AgentInteractiveEndpointAdapter(owner);
        var connection = CreateConnection();
        await adapter.OnConnectionLifecycleChangedAsync(new IpcConnectionLifecycleNotification(
            connection.ConnectionId,
            IpcConnectionLifecycleState.HandshakeCompleted,
            connection,
            IpcDisconnectKind.None,
            DateTimeOffset.UtcNow));

        var closing = adapter.OnConnectionLifecycleChangedAsync(new IpcConnectionLifecycleNotification(
            connection.ConnectionId,
            IpcConnectionLifecycleState.Disconnected,
            connection,
            IpcDisconnectKind.TransportFailure,
            DateTimeOffset.UtcNow)).AsTask();
        await WaitUntilAsync(() => operations.Contains("session-close-admission"));

        Assert.IsFalse(closing.IsCompleted);
        Assert.IsFalse(operations.Contains("lease-dispose"));
        drainRelease.TrySetResult();
        await closing;

        CollectionAssert.AreEqual(
            new[] { "session-close-admission", "session-dispose", "lease-dispose", "binding-release" },
            operations);
        Assert.IsFalse(adapter.HasActiveSession);
        Assert.AreEqual(1, session.DisposeCount);
        Assert.AreEqual(1, lease.DisposeCount);
    }

    [TestMethod]
    public async Task ReplacementHandshakeDrainsOldBindingBeforeOpeningNewSession()
    {
        var operations = new List<string>();
        var endpoints = DispatchProxy.Create<IEndpoints, TestEndpointsDispatchProxy>();
        var firstSession = new FakeAgentInteractiveBackendSession(endpoints, operations);
        var secondSession = new FakeAgentInteractiveBackendSession(endpoints, operations);
        var sessions = new Queue<FakeAgentInteractiveBackendSession>([firstSession, secondSession]);
        var owner = new FakeWindowsAgentBackendRuntimeOwner
        {
            InteractiveBindingFactory = _ =>
            {
                var session = sessions.Dequeue();
                operations.Add(session == firstSession ? "first-open" : "second-open");
                return Task.FromResult(new AgentInteractiveBackendBinding(
                    session,
                    new FakeAgentBackendRuntimeLease(BackendLifetimeReason.InteractiveUi),
                    () => ValueTask.CompletedTask));
            }
        };
        await using var adapter = new AgentInteractiveEndpointAdapter(owner);
        var first = CreateConnection();
        var replacement = CreateConnection();
        await OpenAsync(adapter, first);

        await OpenAsync(adapter, replacement);

        Assert.AreEqual(1, firstSession.DisposeCount);
        Assert.AreEqual(0, secondSession.DisposeCount);
        Assert.IsTrue(adapter.IsReady(replacement));
        CollectionAssert.AreEqual(
            new[] { "first-open", "session-close-admission", "session-dispose", "second-open" },
            operations);
    }

    [TestMethod]
    public async Task RestartRequirementIsForwardedToAgentRuntimeOwner()
    {
        var owner = new FakeWindowsAgentBackendRuntimeOwner();
        await using var adapter = new AgentInteractiveEndpointAdapter(owner);
        var failure = new InvalidOperationException("unsafe runtime state");

        adapter.RequireProcessRestart(failure);

        Assert.AreEqual(1, owner.RequireRestartCount);
        Assert.IsTrue(owner.Snapshot.RequiresProcessRestart);
        Assert.AreSame(failure, owner.Snapshot.Failure);
    }

    [TestMethod]
    public async Task StaleDisconnectCannotCloseNewerConnectionBinding()
    {
        var endpoints = DispatchProxy.Create<IEndpoints, TestEndpointsDispatchProxy>();
        var firstSession = new FakeAgentInteractiveBackendSession(endpoints);
        var secondSession = new FakeAgentInteractiveBackendSession(endpoints);
        var sessions = new Queue<FakeAgentInteractiveBackendSession>([firstSession, secondSession]);
        var owner = new FakeWindowsAgentBackendRuntimeOwner
        {
            InteractiveBindingFactory = _ =>
            {
                var session = sessions.Dequeue();
                return Task.FromResult(new AgentInteractiveBackendBinding(
                    session,
                    new FakeAgentBackendRuntimeLease(BackendLifetimeReason.InteractiveUi),
                    () => ValueTask.CompletedTask));
            }
        };
        await using var adapter = new AgentInteractiveEndpointAdapter(owner);
        var first = CreateConnection();
        var second = CreateConnection();
        await OpenAsync(adapter, first);
        await CloseAsync(adapter, first);
        await OpenAsync(adapter, second);

        await CloseAsync(adapter, first);

        Assert.IsTrue(adapter.IsReady(second));
        Assert.AreEqual(0, secondSession.DisposeCount);
    }

    private static Task OpenAsync(
        AgentInteractiveEndpointAdapter adapter,
        IpcConnectionContext connection) =>
        adapter.OnConnectionLifecycleChangedAsync(new IpcConnectionLifecycleNotification(
            connection.ConnectionId,
            IpcConnectionLifecycleState.HandshakeCompleted,
            connection,
            IpcDisconnectKind.None,
            DateTimeOffset.UtcNow)).AsTask();

    private static Task CloseAsync(
        AgentInteractiveEndpointAdapter adapter,
        IpcConnectionContext connection) =>
        adapter.OnConnectionLifecycleChangedAsync(new IpcConnectionLifecycleNotification(
            connection.ConnectionId,
            IpcConnectionLifecycleState.Disconnected,
            connection,
            IpcDisconnectKind.Clean,
            DateTimeOffset.UtcNow)).AsTask();

    private static IpcConnectionContext CreateConnection() => new(
        Guid.NewGuid(),
        IpcPeerRole.Ui,
        100,
        2,
        Guid.NewGuid(),
        IpcCapabilities.EndpointRpc);

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        while (!condition())
            await Task.Delay(10, timeout.Token);
    }
}
