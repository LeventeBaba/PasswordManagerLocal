using Microsoft.VisualStudio.TestTools.UnitTesting;
using PasswordManagerLocal.Windows.Ipc.Contracts;
using PasswordManagerLocal.Windows.Ipc.Protocol;
using PasswordManagerLocal.Windows.Ipc.Serialization;
using PasswordManagerLocal.Windows.Ipc.Validation;
using System.Buffers.Binary;
using System.Text;

namespace PasswordManagerLocal.Windows.Tests.IPC.Protocol;

[TestClass]
public sealed class IpcFramePayloadCodecTests
{
    [TestMethod]
    public void RequestPayloadStartsWithOperationIdThenRawOperationBody()
    {
        var serializer = new WindowsIpcSerializer();
        var codec = new IpcFramePayloadCodec(serializer);
        var body = Encoding.UTF8.GetBytes("{\"isEnabled\":true}");

        var payload = codec.EncodeRequest(
            new IpcRequestEnvelope(IpcOperationId.SetBackgroundSyncEnabled, body),
            17);

        Assert.AreEqual((int)IpcOperationId.SetBackgroundSyncEnabled,
            BinaryPrimitives.ReadInt32BigEndian(payload.AsSpan(0, sizeof(int))));
        CollectionAssert.AreEqual(body, payload.AsSpan(sizeof(int)).ToArray());
        var decoded = codec.DecodeRequest(17, payload);
        Assert.AreEqual(IpcOperationId.SetBackgroundSyncEnabled, decoded.OperationId);
        CollectionAssert.AreEqual(body, decoded.Payload!);
        CollectionAssert.AreEqual(payload, codec.EncodeRequest(decoded, 17));
    }

    [TestMethod]
    public void NoPayloadRequestAndVoidSuccessUseOnlyTheirRequiredPrefix()
    {
        var codec = new IpcFramePayloadCodec(new WindowsIpcSerializer());
        var request = codec.EncodeRequest(new IpcRequestEnvelope(IpcOperationId.Ping, null), 3);
        var response = codec.EncodeResponse(IpcResponseEnvelope.Success(), 3);

        Assert.AreEqual(sizeof(int), request.Length);
        Assert.AreEqual((int)IpcOperationId.Ping, BinaryPrimitives.ReadInt32BigEndian(request));
        Assert.AreEqual(sizeof(byte), response.Length);
        Assert.AreEqual((byte)IpcResponsePayloadKind.Success, response[0]);
        Assert.IsTrue(codec.DecodeResponse(3, response).IsSuccess);
    }

    [TestMethod]
    public void ResponseStatusOwnsErrorKindAndSuccessBodyRemainsRaw()
    {
        var serializer = new WindowsIpcSerializer();
        var codec = new IpcFramePayloadCodec(serializer);
        var body = Encoding.UTF8.GetBytes("{\"accepted\":true}");
        var encoded = codec.EncodeResponse(IpcResponseEnvelope.Success(body), 42);

        Assert.AreEqual((byte)IpcResponsePayloadKind.Success, encoded[0]);
        CollectionAssert.AreEqual(body, encoded.AsSpan(1).ToArray());

        var decodedError = new IpcError(
            IpcErrorCode.RequestRejected,
            IpcErrorCategory.Validation,
            "The request was rejected.",
            DateTimeOffset.UtcNow,
            IsRetryable: false,
            RequiresProcessRestart: false);
        var failure = codec.EncodeResponse(IpcResponseEnvelope.Failure(decodedError), 42);
        Assert.AreEqual((byte)IpcResponsePayloadKind.Failure, failure[0]);
        var failureJson = Encoding.UTF8.GetString(failure, 1, failure.Length - 1);
        Assert.IsFalse(failureJson.Contains("correlationId", StringComparison.OrdinalIgnoreCase));
        Assert.AreEqual(decodedError, codec.DecodeResponse(42, failure).Error);
    }

    [TestMethod]
    public void EndpointFailureAndLargeResultUseOnlyOuterResponseStatus()
    {
        var codec = new IpcFramePayloadCodec(new WindowsIpcSerializer());
        var endpointErrorJson = Encoding.UTF8.GetBytes("{\"errorCode\":1}");
        var endpointFailure = codec.EncodeResponse(IpcResponseEnvelope.EndpointFailure(endpointErrorJson), 10);
        Assert.AreEqual((byte)IpcResponsePayloadKind.EndpointFailure, endpointFailure[0]);
        CollectionAssert.AreEqual(endpointErrorJson, endpointFailure.AsSpan(1).ToArray());

        var descriptorJson = Encoding.UTF8.GetBytes("{\"transferId\":\"test\"}");
        var largeResult = codec.EncodeResponse(IpcResponseEnvelope.LargeResult(descriptorJson), 11);
        Assert.AreEqual((byte)IpcResponsePayloadKind.LargeResult, largeResult[0]);
        CollectionAssert.AreEqual(descriptorJson, largeResult.AsSpan(1).ToArray());
    }

    [TestMethod]
    public void UnknownOperationAndResponseStatusAreRejected()
    {
        var codec = new IpcFramePayloadCodec(new WindowsIpcSerializer());
        var request = new byte[sizeof(int)];
        BinaryPrimitives.WriteInt32BigEndian(request, int.MaxValue);
        Assert.ThrowsExactly<IpcPayloadException>(() => codec.DecodeRequest(1, request));
        Assert.ThrowsExactly<IpcPayloadException>(() => codec.DecodeResponse(1, new byte[] { byte.MaxValue }));
        Assert.ThrowsExactly<IpcPayloadException>(() => codec.EncodeResponse(
            IpcResponseEnvelope.EndpointFailure(Array.Empty<byte>()), 1));
        Assert.ThrowsExactly<IpcPayloadException>(() => codec.DecodeResponse(1,
            new byte[] { (byte)IpcResponsePayloadKind.EndpointFailure }));
    }
}
