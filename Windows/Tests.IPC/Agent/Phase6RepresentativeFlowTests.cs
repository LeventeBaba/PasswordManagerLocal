using Microsoft.VisualStudio.TestTools.UnitTesting;
using PasswordManagerLocal.Common.Contracts.Endpoints;
using PasswordManagerLocal.Common.Contracts.Responses;
using PasswordManagerLocal.Common.Contracts.Runtime;
using PasswordManagerLocal.Common.Contracts.BackgroundSync;
using PasswordManagerLocal.Windows.Agent.Backend;
using PasswordManagerLocal.Windows.Agent.Endpoint;
using PasswordManagerLocal.Windows.Agent.Hosting;
using PasswordManagerLocal.Windows.EndpointRpc.Authorization;
using PasswordManagerLocal.Windows.EndpointRpc.Contracts;
using PasswordManagerLocal.Windows.EndpointRpc.Contracts.Requests;
using PasswordManagerLocal.Windows.EndpointRpc.Contracts.Responses;
using PasswordManagerLocal.Windows.EndpointRpc.Serialization;
using PasswordManagerLocal.Windows.EndpointRpc.Server;
using PasswordManagerLocal.Windows.EndpointRpc.Validation;
using PasswordManagerLocal.Windows.Ipc.Lifecycle;
using PasswordManagerLocal.Windows.Ipc.Protocol;
using PasswordManagerLocal.Windows.Tests.IPC.Infrastructure;
using System.Reflection;

namespace PasswordManagerLocal.Windows.Tests.IPC.Agent;

[TestClass]
public sealed class Phase6RepresentativeFlowTests
{
    [TestMethod]
    public async Task RegisteredUiAuthorizesEndpointOpensInteractiveBindingAndReturnsResponse()
    {
        var instanceId = Guid.NewGuid();
        var processId = 2314;
        var windowsSessionId = 6;
        var coordinator = new SingleUiConnectionCoordinator();
        var controlConnection = CreateConnection(
            Guid.NewGuid(),
            processId,
            windowsSessionId,
            instanceId,
            IpcCapabilities.Control | IpcCapabilities.Status);
        Assert.IsTrue(coordinator.TryRegister(controlConnection, out _));

        var state = new WindowsAgentStateStore();
        state.MarkRunning(DateTimeOffset.UtcNow);
        var admissionGate = new WindowsAgentAdmissionGate();
        admissionGate.Open();
        using var resolver = new RegisteredUiEndpointRegistrationResolver(
            coordinator,
            admissionGate);
        var endpointConnection = CreateConnection(
            Guid.NewGuid(),
            processId,
            windowsSessionId,
            instanceId,
            IpcCapabilities.EndpointRpc);

        var expectedDeviceId = Guid.NewGuid();
        var endpoints = DispatchProxy.Create<IEndpoints, TestEndpointsDispatchProxy>();
        var endpointProxy = (TestEndpointsDispatchProxy)(object)endpoints;
        endpointProxy.Handler = (method, _) => method.Name switch
        {
            nameof(IEndpoints.GetLocalDeviceInfoAsync) => Task.FromResult(new LocalDeviceInfoResponse
            {
                DeviceId = expectedDeviceId,
                TlsCertFingerprint = "test-fingerprint",
                CreatedAt = DateTimeOffset.UtcNow
            }),
            _ => throw new NotSupportedException(method.Name)
        };
        var session = new FakeAgentInteractiveBackendSession(endpoints);
        var lease = new FakeAgentBackendRuntimeLease(BackendLifetimeReason.InteractiveUi);
        var owner = new FakeWindowsAgentBackendRuntimeOwner
        {
            InteractiveBindingFactory = _ => Task.FromResult(
                new AgentInteractiveBackendBinding(session, lease, () => ValueTask.CompletedTask))
        };
        var admissionPolicy = new WindowsAgentEndpointAdmissionPolicy(
            admissionGate,
            state,
            owner,
            () => WindowsAgentEndpointHostState.Ready);
        var connectionAuthorizer = new EndpointRpcConnectionAuthorizer(
            resolver,
            admissionPolicy);
        Assert.IsTrue(connectionAuthorizer.Authorize(endpointConnection).IsAuthorized);
        await using var adapter = new AgentInteractiveEndpointAdapter(owner);
        await adapter.OnConnectionLifecycleChangedAsync(new IpcConnectionLifecycleNotification(
            endpointConnection.ConnectionId,
            IpcConnectionLifecycleState.HandshakeCompleted,
            endpointConnection,
            IpcDisconnectKind.None,
            DateTimeOffset.UtcNow));

        var serializer = new EndpointRpcSerializer();
        var validator = new EndpointRpcContractValidator();
        await using var dispatcher = new EndpointRpcDispatcher(
            adapter,
            serializer,
            validator,
            new EndpointRpcBackendErrorMapper(),
            adapter);
        var requestPayload = serializer.Serialize(
            new GetLocalDeviceInfoEndpointRequest(),
            EndpointRpcJsonContext.Default.GetLocalDeviceInfoEndpointRequest);
        var result = await dispatcher.DispatchAsync(
            new EndpointRequestContext(
                endpointConnection.ConnectionId,
                42,
                EndpointOperationId.GetLocalDeviceInfo,
                endpointConnection.PeerRole,
                endpointConnection.PeerProcessId,
                endpointConnection.PeerSessionId,
                CancellationToken.None),
            requestPayload,
            CancellationToken.None);

        Assert.IsTrue(result.IsSuccess);
        Assert.IsNotNull(result.Result);
        var response = serializer.Deserialize(
            result.Result!,
            EndpointRpcJsonContext.Default.GetLocalDeviceInfoEndpointResponse);
        Assert.AreEqual(expectedDeviceId, response.Device.DeviceId);
        Assert.AreEqual(1, owner.OpenBindingCount);
        Assert.IsTrue(adapter.AcceptsNewOperations);

        var disconnect = new IpcConnectionLifecycleNotification(
            endpointConnection.ConnectionId,
            IpcConnectionLifecycleState.Disconnected,
            endpointConnection,
            IpcDisconnectKind.Clean,
            DateTimeOffset.UtcNow);
        await adapter.OnConnectionLifecycleChangedAsync(disconnect);
        await connectionAuthorizer.OnConnectionLifecycleChangedAsync(disconnect);

        Assert.AreEqual(1, session.DisposeCount);
        Assert.AreEqual(1, lease.DisposeCount);
        Assert.IsFalse(adapter.HasActiveSession);
    }


    [TestMethod]
    public async Task DisconnectDuringAdmittedMutationClosesAdmissionDrainsAndReleasesInteractiveLease()
    {
        var endpointConnection = CreateConnection(
            Guid.NewGuid(),
            processId: 2314,
            windowsSessionId: 6,
            instanceId: Guid.NewGuid(),
            capabilities: IpcCapabilities.EndpointRpc);
        var mutationStarted = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var mutationRelease = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var sessionDrainRelease = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var endpoints = DispatchProxy.Create<IEndpoints, TestEndpointsDispatchProxy>();
        var endpointProxy = (TestEndpointsDispatchProxy)(object)endpoints;
        endpointProxy.Handler = (method, _) => method.Name switch
        {
            nameof(IEndpoints.LogoutAsync) => CompleteMutationAsync(
                mutationStarted,
                mutationRelease),
            _ => throw new NotSupportedException(method.Name)
        };
        var operations = new List<string>();
        var session = new FakeAgentInteractiveBackendSession(
            endpoints,
            operations,
            sessionDrainRelease)
        {
            ActiveOperationCount = 1
        };
        var lease = new FakeAgentBackendRuntimeLease(
            BackendLifetimeReason.InteractiveUi,
            operations);
        var owner = new FakeWindowsAgentBackendRuntimeOwner
        {
            InteractiveBindingFactory = _ => Task.FromResult(
                new AgentInteractiveBackendBinding(session, lease, () => ValueTask.CompletedTask))
        };
        await using var adapter = new AgentInteractiveEndpointAdapter(owner);
        await adapter.OnConnectionLifecycleChangedAsync(new IpcConnectionLifecycleNotification(
            endpointConnection.ConnectionId,
            IpcConnectionLifecycleState.HandshakeCompleted,
            endpointConnection,
            IpcDisconnectKind.None,
            DateTimeOffset.UtcNow));

        var serializer = new EndpointRpcSerializer();
        await using var dispatcher = new EndpointRpcDispatcher(
            adapter,
            serializer,
            new EndpointRpcContractValidator(),
            new EndpointRpcBackendErrorMapper(),
            adapter);
        var requestPayload = serializer.Serialize(
            new LogoutEndpointRequest { Token = Guid.NewGuid() },
            EndpointRpcJsonContext.Default.LogoutEndpointRequest);
        using var transportLoss = new CancellationTokenSource();
        var dispatch = dispatcher.DispatchAsync(
            new EndpointRequestContext(
                endpointConnection.ConnectionId,
                43,
                EndpointOperationId.Logout,
                endpointConnection.PeerRole,
                endpointConnection.PeerProcessId,
                endpointConnection.PeerSessionId,
                transportLoss.Token),
            requestPayload,
            transportLoss.Token);
        await mutationStarted.Task;

        transportLoss.Cancel();
        var disconnect = adapter.OnConnectionLifecycleChangedAsync(
            new IpcConnectionLifecycleNotification(
                endpointConnection.ConnectionId,
                IpcConnectionLifecycleState.Disconnected,
                endpointConnection,
                IpcDisconnectKind.TransportFailure,
                DateTimeOffset.UtcNow)).AsTask();
        await WaitUntilAsync(() => operations.Contains("session-close-admission"));

        Assert.IsFalse(adapter.AcceptsNewOperations);
        Assert.IsFalse(dispatch.IsCompleted);
        Assert.IsFalse(disconnect.IsCompleted);

        mutationRelease.TrySetResult();
        var result = await dispatch;
        Assert.IsTrue(result.IsSuccess);
        Assert.IsFalse(disconnect.IsCompleted);

        sessionDrainRelease.TrySetResult();
        await disconnect;

        CollectionAssert.AreEqual(
            new[] { "session-close-admission", "session-dispose", "lease-dispose" },
            operations);
        Assert.AreEqual(1, session.DisposeCount);
        Assert.AreEqual(1, lease.DisposeCount);
        Assert.IsFalse(adapter.HasActiveSession);
    }

    private static IpcConnectionContext CreateConnection(
        Guid connectionId,
        int processId,
        int windowsSessionId,
        Guid instanceId,
        IpcCapabilities capabilities) => new(
            connectionId,
            IpcPeerRole.Ui,
            processId,
            windowsSessionId,
            instanceId,
            capabilities);

    private static async Task CompleteMutationAsync(
        TaskCompletionSource started,
        TaskCompletionSource release)
    {
        started.TrySetResult();
        await release.Task;
    }

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        while (!condition())
            await Task.Delay(10, timeout.Token);
    }
}
