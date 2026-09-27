using Microsoft.VisualStudio.TestTools.UnitTesting;
using PasswordManagerLocal.Windows.EndpointRpc.Contracts;
using PasswordManagerLocal.Windows.EndpointRpc.Metadata;
using PasswordManagerLocal.Windows.EndpointRpc.Serialization;
using System.Text.Json;

namespace PasswordManagerLocal.Windows.Tests.EndpointRpc.Serialization;

[TestClass]
public sealed class EndpointRpcSerializationTests
{
    [TestMethod]
    public void EveryMappedRequestAndResponseUsesSourceGeneratedMetadata()
    {
        foreach (var descriptor in EndpointOperationManifest.All)
        {
            RoundTrip(descriptor.RequestType);
            RoundTrip(descriptor.ResponseType);
        }
    }

    [TestMethod]
    public void MalformedJsonIsRejected()
    {
        var serializer = new EndpointRpcSerializer();
        Assert.ThrowsExactly<EndpointRpcPayloadException>(() =>
            serializer.Deserialize(
                "{"u8.ToArray(),
                EndpointRpcJsonContext.Default.LoginEndpointRequest));
    }


    [TestMethod]
    public void UnknownJsonMemberIsRejected()
    {
        var serializer = new EndpointRpcSerializer();
        Assert.ThrowsExactly<EndpointRpcPayloadException>(() =>
            serializer.Deserialize(
                "{\"token\":\"00000000-0000-0000-0000-000000000001\",\"unexpected\":true}"u8.ToArray(),
                EndpointRpcJsonContext.Default.GetSavedPasswordsEndpointRequest));
    }

    [TestMethod]
    public void LargeTransferContractsRoundTripWithSourceGeneratedMetadata()
    {
        RoundTrip(typeof(PasswordManagerLocal.Windows.EndpointRpc.Contracts.LargeTransfer.EndpointLargeResultDescriptor));
        RoundTrip(typeof(PasswordManagerLocal.Windows.EndpointRpc.Contracts.LargeTransfer.GetEndpointLargeResultChunkRequest));
        RoundTrip(typeof(PasswordManagerLocal.Windows.EndpointRpc.Contracts.LargeTransfer.GetEndpointLargeResultChunkResponse));
        RoundTrip(typeof(PasswordManagerLocal.Windows.EndpointRpc.Contracts.LargeTransfer.ReleaseEndpointLargeResultRequest));
        RoundTrip(typeof(PasswordManagerLocal.Windows.EndpointRpc.Contracts.LargeTransfer.ReleaseEndpointLargeResultResponse));
    }


    [TestMethod]
    public void PartialCommitErrorRoundTripsWithSafeRecoveryMetadata()
    {
        var serializer = new EndpointRpcSerializer();
        var error = new EndpointRpcError(
            EndpointRpcErrorCode.OperationPartiallyCommitted,
            EndpointRpcErrorCategory.Recovery,
            "The device addition was committed, but enrollment recovery is required.",
            1,
            DateTimeOffset.UtcNow,
            IsRetryable: false,
            RequiresProcessRestart: true,
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
        var roundTrip = serializer.Deserialize(payload, EndpointRpcJsonContext.Default.EndpointRpcError);

        Assert.AreEqual(error, roundTrip);
        Assert.IsNotNull(EndpointRpcJsonContext.Default.GetTypeInfo(typeof(EndpointRecoveryMetadata)));
        Array.Clear(payload);
    }

    private static void RoundTrip(Type type)
    {
        var metadata = EndpointRpcJsonContext.Default.GetTypeInfo(type);
        Assert.IsNotNull(metadata, type.FullName);
        var value = Activator.CreateInstance(type);
        Assert.IsNotNull(value, type.FullName);
        var payload = JsonSerializer.SerializeToUtf8Bytes(value, metadata);
        var result = JsonSerializer.Deserialize(payload, metadata);
        Assert.IsNotNull(result, type.FullName);
        Assert.AreEqual(type, result.GetType());
    }
}
