using Microsoft.VisualStudio.TestTools.UnitTesting;
using PasswordManagerLocal.Windows.EndpointRpc.Contracts;
using PasswordManagerLocal.Windows.EndpointRpc.Contracts.LargeTransfer;
using PasswordManagerLocal.Windows.EndpointRpc.Serialization;
using PasswordManagerLocal.Windows.Ipc.Contracts;
using PasswordManagerLocal.Windows.Ipc.Protocol;
using PasswordManagerLocal.Windows.Ipc.Serialization;
using PasswordManagerLocal.Windows.Ipc.Validation;

namespace PasswordManagerLocal.Windows.Tests.EndpointRpc.Payload;

[TestClass]
public sealed class EndpointRpcPayloadLimitTests
{
    [TestMethod]
    public void MaximumEndpointPayloadsFitHardenedOuterTransportBounds()
    {
        Assert.IsTrue(EndpointRpcLimits.MaximumRequestPayloadSize + 5 <= IpcContractLimits.MaximumInnerPayloadSize);
        Assert.IsTrue(EndpointRpcLimits.MaximumResponsePayloadSize + 1 <= IpcContractLimits.MaximumInnerPayloadSize);
        Assert.IsTrue(IpcContractLimits.MaximumInnerPayloadSize < WindowsIpcProtocol.MaximumPayloadSize);
    }


    [TestMethod]
    public void MaximumEndpointPayloadsAreAcceptedByTheInnerCodec()
    {
        var codec = new EndpointRpcMessageCodec(new EndpointRpcSerializer());
        var request = codec.EncodeRequest(
            EndpointOperationId.AddNewPassword,
            new byte[EndpointRpcLimits.MaximumRequestPayloadSize]);
        var response = codec.EncodeSuccess(
            new byte[EndpointRpcLimits.MaximumResponsePayloadSize]);

        Assert.AreEqual(EndpointRpcLimits.MaximumRequestPayloadSize + 5, request.Length);
        Assert.AreEqual(EndpointRpcLimits.MaximumResponsePayloadSize + 1, response.Length);
    }

    [TestMethod]
    public void RequestAndResponseCodecRejectOversizedPayloads()
    {
        var codec = new EndpointRpcMessageCodec(new EndpointRpcSerializer());
        Assert.ThrowsExactly<EndpointRpcPayloadException>(() =>
            codec.EncodeRequest(
                EndpointOperationId.Login,
                new byte[EndpointRpcLimits.MaximumRequestPayloadSize + 1]));
        Assert.ThrowsExactly<EndpointRpcPayloadException>(() =>
            codec.EncodeSuccess(
                new byte[EndpointRpcLimits.MaximumResponsePayloadSize + 1]));
    }

    [TestMethod]
    public void OuterValidatorRejectsOversizedEndpointEnvelopeBeforeHandler()
    {
        var validator = new WindowsIpcContractValidator();
        Assert.ThrowsExactly<PasswordManagerLocal.Windows.Ipc.Serialization.IpcPayloadLimitExceededException>(() =>
            validator.Validate(new IpcRequestEnvelope(
                2,
                IpcOperationId.EndpointRpcRequest,
                new byte[IpcContractLimits.MaximumInnerPayloadSize + 1])));
    }

    [TestMethod]
    public void OversizedErrorPayloadIsRejectedBeforeDeserialization()
    {
        var codec = new EndpointRpcMessageCodec(new EndpointRpcSerializer());
        var message = new byte[EndpointRpcLimits.MaximumErrorPayloadSize + 2];
        message[0] = 0;

        Assert.ThrowsExactly<EndpointRpcPayloadException>(() => codec.DecodeResponse(message, 1));
    }

    [TestMethod]
    public void MaximumChunkResponseRemainsInsideOrdinaryEndpointFrame()
    {
        var serializer = new EndpointRpcSerializer();
        var codec = new EndpointRpcMessageCodec(serializer);
        var response = new GetEndpointLargeResultChunkResponse
        {
            TransferId = Guid.NewGuid(),
            OriginalCorrelationId = 99,
            ChunkIndex = 0,
            IsFinal = true,
            DeclaredTotalLength = EndpointRpcLimits.MaximumLargeResultChunkBytes,
            DeclaredChunkCount = 1,
            ChunkPayload = new byte[EndpointRpcLimits.MaximumLargeResultChunkBytes]
        };
        var serialized = serializer.Serialize(
            response,
            EndpointRpcJsonContext.Default.GetEndpointLargeResultChunkResponse);
        var encoded = codec.EncodeSuccess(serialized);
        var outer = new WindowsIpcSerializer().Serialize(
            IpcResponseEnvelope.Success(1, encoded),
            WindowsIpcJsonContext.Default.IpcResponseEnvelope);

        Assert.IsTrue(serialized.Length <= EndpointRpcLimits.MaximumLargeResultChunkResponsePayloadSize);
        Assert.IsTrue(EndpointRpcLimits.MaximumLargeResultChunkResponsePayloadSize <= EndpointRpcLimits.MaximumResponsePayloadSize);
        Assert.IsTrue(encoded.Length <= IpcContractLimits.MaximumInnerPayloadSize);
        Assert.IsTrue(outer.Length <= WindowsIpcProtocol.MaximumPayloadSize);
        Array.Clear(serialized);
        Array.Clear(encoded);
        Array.Clear(outer);
        Array.Clear(response.ChunkPayload);
    }

}
