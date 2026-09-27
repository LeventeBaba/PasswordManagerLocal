using Microsoft.VisualStudio.TestTools.UnitTesting;
using PasswordManagerLocal.Windows.EndpointRpc.Client;
using PasswordManagerLocal.Windows.EndpointRpc.Contracts;
using PasswordManagerLocal.Windows.EndpointRpc.Contracts.Requests;
using PasswordManagerLocal.Windows.EndpointRpc.Contracts.Responses;
using PasswordManagerLocal.Windows.EndpointRpc.Serialization;
using PasswordManagerLocal.Windows.EndpointRpc.Security;
using PasswordManagerLocal.Windows.EndpointRpc.Server;
using PasswordManagerLocal.Windows.Tests.EndpointRpc.Infrastructure;
using PasswordManagerLocal.Windows.EndpointRpc.Validation;
using PasswordManagerLocal.Windows.Ipc.Contracts;
using PasswordManagerLocal.Windows.Ipc.Lifecycle;
using PasswordManagerLocal.Windows.Ipc.Protocol;
using PasswordManagerLocal.Windows.Ipc.Serialization;
using PasswordManagerLocal.Windows.Ipc.Server;

namespace PasswordManagerLocal.Windows.Tests.EndpointRpc.Security;

[TestClass]
public sealed class EndpointRpcSensitiveDataTests
{
    [TestMethod]
    public async Task ClientClearsTemporarySerializedSensitivePayloadAfterFailure()
    {
        byte[]? observedPayload = null;
        var transport = new RecordingEndpointRpcTransport((_, payload, _) =>
        {
            observedPayload = payload;
            return Task.FromException<byte[]>(new EndpointRpcRemoteException(
                new EndpointRpcError(
                    EndpointRpcErrorCode.BackendFailure,
                    EndpointRpcErrorCategory.Internal,
                    "The endpoint operation failed.",
                    1,
                    DateTimeOffset.UtcNow,
                    false,
                    false,
                    EndpointMutationOutcome.NotCommitted,
                    Recovery: null)));
        });
        var proxy = new NamedPipeEndpointsProxy(
            transport,
            new EndpointRpcSerializer(),
            new EndpointRpcContractValidator());

        await Assert.ThrowsExactlyAsync<EndpointRpcRemoteException>(() =>
            proxy.DeleteUserAccountAsync(EndpointRpcTestData.Token, EndpointRpcTestData.Password()));

        Assert.IsNotNull(observedPayload);
        Assert.IsTrue(observedPayload.All(value => value == 0));
    }

    [TestMethod]
    public void UntransferredSensitiveResponseBufferIsCleared()
    {
        var response = new GetUnsecurePasswordEndpointResponse
        {
            Password = EndpointRpcTestData.Password()
        };

        EndpointSensitiveData.ClearResponse(EndpointOperationId.GetUnsecurePassword, response);

        Assert.IsTrue(response.Password.All(value => value == 0));
    }

    [TestMethod]
    public async Task ServerClearsEndpointMessageAfterHandlingSensitiveRequest()
    {
        var serializer = new EndpointRpcSerializer();
        var validator = new EndpointRpcContractValidator();
        var codec = new EndpointRpcMessageCodec(serializer);
        await using var dispatcher = new EndpointRpcDispatcher(
            new FixedEndpointRpcEndpointAdapter(new ThrowingRecordingEndpoints()),
            serializer,
            validator,
            new EndpointRpcBackendErrorMapper());
        var handler = new EndpointRpcWindowsIpcRequestHandler(
            dispatcher,
            codec,
            validator,
            new ControllableEndpointRpcAdmissionPolicy());
        var requestPayload = serializer.Serialize(
            new DeleteUserAccountEndpointRequest
            {
                Token = EndpointRpcTestData.Token,
                Password = EndpointRpcTestData.Password()
            },
            EndpointRpcJsonContext.Default.DeleteUserAccountEndpointRequest);
        var endpointMessage = codec.EncodeRequest(EndpointOperationId.DeleteUserAccount, requestPayload);
        var context = CreateContext(endpointMessage);

        var response = await handler.HandleAsync(context, CancellationToken.None);

        Assert.IsTrue(response.IsSuccess);
        Assert.IsTrue(endpointMessage.All(value => value == 0));
        Array.Clear(requestPayload);
        Array.Clear(response.Result!);
    }

    [TestMethod]
    public async Task ServerClearsBackendPasswordBufferAfterResponseSerialization()
    {
        var endpoints = new SensitivePasswordEndpoints();
        var serializer = new EndpointRpcSerializer();
        var validator = new EndpointRpcContractValidator();
        await using var dispatcher = new EndpointRpcDispatcher(
            new FixedEndpointRpcEndpointAdapter(endpoints),
            serializer,
            validator,
            new EndpointRpcBackendErrorMapper());
        var request = serializer.Serialize(
            new GetUnsecurePasswordEndpointRequest
            {
                Token = EndpointRpcTestData.Token,
                PasswordId = EndpointRpcTestData.ItemId
            },
            EndpointRpcJsonContext.Default.GetUnsecurePasswordEndpointRequest);

        var result = await dispatcher.DispatchAsync(
            new EndpointRequestContext(
                Guid.NewGuid(),
                70,
                EndpointOperationId.GetUnsecurePassword,
                IpcPeerRole.Ui,
                1234,
                Guid.NewGuid(),
                CancellationToken.None),
            request,
            CancellationToken.None);

        Assert.IsTrue(result.IsSuccess);
        Assert.IsTrue(endpoints.PasswordBuffer.All(value => value == 0));
        Array.Clear(request);
        Array.Clear(result.Result!);
    }


    [TestMethod]
    public async Task ServerClearsInvalidBackendPasswordBufferDuringResponseValidationFailure()
    {
        var sensitiveBuffer = Enumerable.Repeat(
            (byte)0x5A,
            EndpointRpcLimits.MaximumSensitiveBinaryFieldSize + 1).ToArray();
        var endpoints = new SensitivePasswordEndpoints(sensitiveBuffer);
        var serializer = new EndpointRpcSerializer();
        await using var dispatcher = new EndpointRpcDispatcher(
            new FixedEndpointRpcEndpointAdapter(endpoints),
            serializer,
            new EndpointRpcContractValidator(),
            new EndpointRpcBackendErrorMapper());
        var request = serializer.Serialize(
            new GetUnsecurePasswordEndpointRequest
            {
                Token = EndpointRpcTestData.Token,
                PasswordId = EndpointRpcTestData.ItemId
            },
            EndpointRpcJsonContext.Default.GetUnsecurePasswordEndpointRequest);

        var result = await dispatcher.DispatchAsync(
            new EndpointRequestContext(
                Guid.NewGuid(),
                71,
                EndpointOperationId.GetUnsecurePassword,
                IpcPeerRole.Ui,
                1234,
                Guid.NewGuid(),
                CancellationToken.None),
            request,
            CancellationToken.None);

        Assert.IsFalse(result.IsSuccess);
        Assert.AreEqual(EndpointRpcErrorCode.ValidationFailed, result.Error!.ErrorCode);
        Assert.IsTrue(sensitiveBuffer.All(value => value == 0));
        Array.Clear(request);
    }


    [TestMethod]
    public void PartialCommitRecoveryMetadataContainsOnlySafeIdentifiersAndFlags()
    {
        var serializer = new EndpointRpcSerializer();
        var error = new EndpointRpcError(
            EndpointRpcErrorCode.OperationPartiallyCommitted,
            EndpointRpcErrorCategory.Recovery,
            "The device addition was committed, but enrollment recovery is required.",
            1,
            DateTimeOffset.UtcNow,
            IsRetryable: false,
            RequiresProcessRestart: false,
            EndpointMutationOutcome.PartiallyCommittedRecoveryRequired,
            new EndpointRecoveryMetadata(
                EndpointRecoveryKind.DeviceEnrollment,
                Guid.Parse("22222222-2222-2222-2222-222222222222"),
                Guid.Parse("33333333-3333-3333-3333-333333333333"),
                Guid.Parse("11111111-1111-1111-1111-111111111111"),
                RecoveryAvailable: true,
                TransferPending: true,
                RequiresSignedRemovalToUndo: true));

        var payload = serializer.Serialize(error, EndpointRpcJsonContext.Default.EndpointRpcError);
        var json = System.Text.Encoding.UTF8.GetString(payload);

        Assert.IsFalse(json.Contains("password", StringComparison.OrdinalIgnoreCase));
        Assert.IsFalse(json.Contains("token", StringComparison.OrdinalIgnoreCase));
        Assert.IsFalse(json.Contains("secret", StringComparison.OrdinalIgnoreCase));
        Assert.IsFalse(json.Contains("key", StringComparison.OrdinalIgnoreCase));
        Assert.IsFalse(json.Contains("snapshot", StringComparison.OrdinalIgnoreCase));
        Assert.IsFalse(json.Contains("path", StringComparison.OrdinalIgnoreCase));
        Array.Clear(payload);
    }

    private static IpcRequestContext CreateContext(byte[] endpointMessage) =>
        new(
            new IpcConnectionContext(
                Guid.NewGuid(),
                IpcPeerRole.Ui,
                1234,
                0,
                Guid.NewGuid(),
                IpcCapabilities.EndpointRpc),
            new IpcRequestEnvelope(60, IpcOperationId.EndpointRpcRequest, endpointMessage),
            new WindowsIpcSerializer());
}
