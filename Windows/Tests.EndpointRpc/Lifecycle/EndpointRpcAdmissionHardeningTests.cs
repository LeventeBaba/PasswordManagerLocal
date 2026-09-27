using Microsoft.VisualStudio.TestTools.UnitTesting;
using PasswordManagerLocal.Windows.EndpointRpc.Contracts;
using PasswordManagerLocal.Windows.EndpointRpc.Contracts.Requests;
using PasswordManagerLocal.Windows.EndpointRpc.Serialization;
using PasswordManagerLocal.Windows.EndpointRpc.Server;
using PasswordManagerLocal.Windows.Tests.EndpointRpc.Infrastructure;
using PasswordManagerLocal.Windows.EndpointRpc.Validation;
using PasswordManagerLocal.Windows.Ipc.Contracts;
using PasswordManagerLocal.Windows.Ipc.Lifecycle;
using PasswordManagerLocal.Windows.Ipc.Protocol;
using PasswordManagerLocal.Windows.Ipc.Serialization;
using PasswordManagerLocal.Windows.Ipc.Server;

namespace PasswordManagerLocal.Windows.Tests.EndpointRpc.Lifecycle;

[TestClass]
public sealed class EndpointRpcAdmissionHardeningTests
{
    [TestMethod]
    public async Task AdmittedRequestDrainsAndLaterRequestIsRejectedBeforeBackendInvocation()
    {
        var endpoints = new BlockingLogoutEndpoints();
        var serializer = new EndpointRpcSerializer();
        var validator = new EndpointRpcContractValidator();
        var codec = new EndpointRpcMessageCodec(serializer);
        var admissionPolicy = new ControllableEndpointRpcAdmissionPolicy();
        await using var dispatcher = new EndpointRpcDispatcher(
            new FixedEndpointRpcEndpointAdapter(endpoints),
            serializer,
            validator,
            new EndpointRpcBackendErrorMapper());
        var handler = new EndpointRpcWindowsIpcRequestHandler(
            dispatcher,
            codec,
            validator,
            admissionPolicy);

        var first = handler.HandleAsync(CreateContext(1, serializer, codec), CancellationToken.None);
        await endpoints.Started;
        Assert.AreEqual(1, admissionPolicy.ActiveRequests);

        admissionPolicy.CanAcceptConnection = false;
        var drain = admissionPolicy.WaitForDrainAsync();
        var rejected = await handler.HandleAsync(
            CreateContext(2, serializer, codec),
            CancellationToken.None);

        Assert.IsFalse(rejected.IsSuccess);
        Assert.AreEqual(IpcErrorCode.AgentUnavailable, rejected.Error!.ErrorCode);
        Assert.AreEqual(1, endpoints.InvocationCount);
        Assert.IsFalse(first.IsCompleted);
        Assert.IsFalse(drain.IsCompleted);

        endpoints.Release();
        var completed = await first;
        await drain;

        Assert.IsTrue(completed.IsSuccess);
        Assert.AreEqual(0, admissionPolicy.ActiveRequests);
        Assert.AreEqual(1, endpoints.InvocationCount);
    }

    [TestMethod]
    public async Task LargeResultChunkAndReleaseRequestsAreRejectedAfterAdmissionCloses()
    {
        var serializer = new EndpointRpcSerializer();
        var validator = new EndpointRpcContractValidator();
        var codec = new EndpointRpcMessageCodec(serializer);
        var admissionPolicy = new ControllableEndpointRpcAdmissionPolicy
        {
            CanAcceptConnection = false
        };
        await using var dispatcher = new EndpointRpcDispatcher(
            new FixedEndpointRpcEndpointAdapter(new BlockingLogoutEndpoints()),
            serializer,
            validator,
            new EndpointRpcBackendErrorMapper());
        var handler = new EndpointRpcWindowsIpcRequestHandler(
            dispatcher,
            codec,
            validator,
            admissionPolicy);
        var connection = CreateConnection();

        var chunk = await handler.HandleAsync(
            CreateContext(
                connection,
                10,
                codec.EncodeLargeResultChunkRequest([])),
            CancellationToken.None);
        var release = await handler.HandleAsync(
            CreateContext(
                connection,
                11,
                codec.EncodeLargeResultReleaseRequest([])),
            CancellationToken.None);

        Assert.AreEqual(IpcErrorCode.AgentUnavailable, chunk.Error!.ErrorCode);
        Assert.AreEqual(IpcErrorCode.AgentUnavailable, release.Error!.ErrorCode);
    }

    private static IpcRequestContext CreateContext(
        long correlationId,
        EndpointRpcSerializer serializer,
        EndpointRpcMessageCodec codec)
    {
        var payload = serializer.Serialize(
            new LogoutEndpointRequest { Token = Guid.NewGuid() },
            EndpointRpcJsonContext.Default.LogoutEndpointRequest);
        var encoded = codec.EncodeRequest(EndpointOperationId.Logout, payload);
        return CreateContext(CreateConnection(), correlationId, encoded);
    }

    private static IpcRequestContext CreateContext(
        IpcConnectionContext connection,
        long correlationId,
        byte[] encoded) =>
        new(
            connection,
            new IpcRequestEnvelope(
                correlationId,
                IpcOperationId.EndpointRpcRequest,
                encoded),
            new WindowsIpcSerializer());

    private static IpcConnectionContext CreateConnection() =>
        new(
            Guid.NewGuid(),
            IpcPeerRole.Ui,
            PeerProcessId: 1234,
            PeerWindowsSessionId: 1,
            PeerSessionId: Guid.NewGuid(),
            PeerCapabilities: IpcCapabilities.EndpointRpc);
}
