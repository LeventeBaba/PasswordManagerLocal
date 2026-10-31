using Microsoft.VisualStudio.TestTools.UnitTesting;
using PasswordManagerLocal.Windows.EndpointRpc.Contracts;
using PasswordManagerLocal.Windows.EndpointRpc.Serialization;
using System.Buffers.Binary;
using System.Text;

namespace PasswordManagerLocal.Windows.Tests.EndpointRpc.Serialization;

[TestClass]
public sealed class EndpointRpcMessageCodecTests
{
    [TestMethod]
    public void EndpointRequestCarriesStableOperationIdThenRawJson()
    {
        var codec = new EndpointRpcMessageCodec(new EndpointRpcSerializer());
        var json = Encoding.UTF8.GetBytes("{\"token\":\"value\"}");

        var message = codec.EncodeRequest(EndpointOperationId.Login, json);

        Assert.AreEqual((int)EndpointOperationId.Login,
            BinaryPrimitives.ReadInt32BigEndian(message.AsSpan(0, sizeof(int))));
        CollectionAssert.AreEqual(json, message.AsSpan(sizeof(int)).ToArray());
        var decoded = codec.DecodeRequest(message);
        Assert.AreEqual(EndpointRpcRequestKind.PublicOperation, decoded.RequestKind);
        Assert.AreEqual(EndpointOperationId.Login, decoded.OperationId);
        CollectionAssert.AreEqual(json, decoded.Payload!);
    }

    [TestMethod]
    public void InternalLargeResultOperationsUseReservedNegativeOperationIds()
    {
        var codec = new EndpointRpcMessageCodec(new EndpointRpcSerializer());
        var chunk = codec.EncodeLargeResultChunkRequest(new byte[] { 1 });
        var release = codec.EncodeLargeResultReleaseRequest(new byte[] { 2 });

        Assert.AreEqual(-1, BinaryPrimitives.ReadInt32BigEndian(chunk));
        Assert.AreEqual(-2, BinaryPrimitives.ReadInt32BigEndian(release));
        Assert.AreEqual(EndpointRpcRequestKind.GetLargeResultChunk, codec.DecodeRequest(chunk).RequestKind);
        Assert.AreEqual(EndpointRpcRequestKind.ReleaseLargeResult, codec.DecodeRequest(release).RequestKind);
    }

    [TestMethod]
    [DataRow(0)]
    [DataRow(-3)]
    [DataRow(42)]
    [DataRow(int.MaxValue)]
    public void UndefinedAndReservedOperationIdsAreRejected(int operationId)
    {
        var codec = new EndpointRpcMessageCodec(new EndpointRpcSerializer());
        var bytes = new byte[sizeof(int)];
        BinaryPrimitives.WriteInt32BigEndian(bytes, operationId);

        Assert.ThrowsExactly<EndpointRpcPayloadException>(() => codec.DecodeRequest(bytes));
    }
}
