using Microsoft.VisualStudio.TestTools.UnitTesting;
using PasswordManagerLocal.Common.Contracts.Runtime;
using PasswordManagerLocal.Common.Contracts.BackgroundSync;
using PasswordManagerLocal.Windows.EndpointRpc.Client;
using PasswordManagerLocal.Windows.EndpointRpc.Contracts;
using PasswordManagerLocal.Windows.EndpointRpc.Metadata;
using PasswordManagerLocal.Windows.EndpointRpc.Serialization;
using PasswordManagerLocal.Windows.EndpointRpc.Server;
using PasswordManagerLocal.Windows.Tests.EndpointRpc.Infrastructure;
using PasswordManagerLocal.Windows.EndpointRpc.Validation;
using PasswordManagerLocal.Windows.Ipc.Protocol;
using System.Text.Json;

namespace PasswordManagerLocal.Windows.Tests.EndpointRpc.Lifecycle;

[TestClass]
public sealed class EndpointRpcCancellationAndLifecycleTests
{
    [TestMethod]
    public async Task CriticalAdmittedOperationIsNotCancelledByTransportDisconnectToken()
    {
        var endpoints = new BlockingLogoutEndpoints();
        await using var dispatcher = new EndpointRpcDispatcher(
            new FixedEndpointRpcEndpointAdapter(endpoints),
            new EndpointRpcSerializer(),
            new EndpointRpcContractValidator(),
            new EndpointRpcBackendErrorMapper());
        var descriptor = EndpointOperationManifest.Get(EndpointOperationId.Logout);
        var request = EndpointRpcTestData.CreateRequest(EndpointOperationId.Logout);
        var payload = JsonSerializer.SerializeToUtf8Bytes(
            request,
            EndpointRpcJsonContext.Default.GetTypeInfo(descriptor.RequestType)!);
        using var cancellationSource = new CancellationTokenSource();
        var dispatchTask = dispatcher.DispatchAsync(
            new EndpointRequestContext(
                Guid.NewGuid(),
                50,
                EndpointOperationId.Logout,
                IpcPeerRole.Ui,
                1234,
                Guid.NewGuid(),
                cancellationSource.Token),
            payload,
            cancellationSource.Token);

        await endpoints.Started;
        cancellationSource.Cancel();
        Assert.IsFalse(dispatchTask.IsCompleted);
        endpoints.Release();
        var result = await dispatchTask;
        Assert.IsTrue(result.IsSuccess);
    }

    [TestMethod]
    public async Task FrontendClientRejectsAccessBeforeConnectAndAfterConnectionLoss()
    {
        var transport = new ControllableEndpointRpcTransport();
        var connector = new FakeEndpointRpcClientConnector(transport);
        await using var client = new WindowsNamedPipeFrontendBackendClient(connector);

        await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => client.GetEndpointsAsync());
        await client.ConnectAsync();
        Assert.AreEqual(BackendRuntimeState.Ready, client.Snapshot.State);
        Assert.IsNotNull(await client.GetEndpointsAsync());

        transport.Disconnect();
        await transport.Completion;
        for (var attempt = 0; attempt < 20 && client.Snapshot.State != BackendRuntimeState.Failed; attempt++)
            await Task.Yield();
        Assert.AreEqual(BackendRuntimeState.Failed, client.Snapshot.State);
        await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => client.GetEndpointsAsync());
        Assert.AreEqual(1, connector.ConnectCount);
    }


    [TestMethod]
    public async Task ExplicitReconnectDisposesStaleTransportWithoutAutomaticReplay()
    {
        var firstTransport = new ControllableEndpointRpcTransport();
        var secondTransport = new ControllableEndpointRpcTransport();
        var connector = new SequenceEndpointRpcClientConnector(firstTransport, secondTransport);
        await using var client = new WindowsNamedPipeFrontendBackendClient(connector);

        await client.ConnectAsync();
        firstTransport.Disconnect();
        await firstTransport.Completion;
        for (var attempt = 0; attempt < 20 && client.Snapshot.State != BackendRuntimeState.Failed; attempt++)
            await Task.Yield();

        await client.ConnectAsync();

        Assert.IsTrue(firstTransport.IsDisposed);
        Assert.AreEqual(2, connector.ConnectCount);
        Assert.AreEqual(BackendRuntimeState.Ready, client.Snapshot.State);
        Assert.IsNotNull(await client.GetEndpointsAsync());
    }

    [TestMethod]
    public async Task FrontendClientDisposalIsIdempotentAndNeverFallsBackToInProcessRuntime()
    {
        var transport = new ControllableEndpointRpcTransport();
        var client = new WindowsNamedPipeFrontendBackendClient(
            new FakeEndpointRpcClientConnector(transport));
        await client.ConnectAsync();

        await client.DisposeAsync();
        await client.DisposeAsync();

        await Assert.ThrowsExactlyAsync<ObjectDisposedException>(() => client.GetEndpointsAsync());
    }
}
