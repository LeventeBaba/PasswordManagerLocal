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
    public void PartialCommitErrorRoundTripsWithOnlySafeFailureMetadata()
    {
        var serializer = new EndpointRpcSerializer();
        var error = new EndpointRpcError(
            EndpointRpcErrorCode.OperationPartiallyCommitted,
            EndpointFailureKind.DeviceEnrollment,
            "The device addition was committed, but enrollment recovery is required.",
            RequiresProcessRestart: true,
            EndpointMutationOutcome.PartiallyCommittedRecoveryRequired,
            DeviceEnrollmentErrorCode.NewDeviceConnectionFailed);

        var payload = serializer.Serialize(error, EndpointRpcJsonContext.Default.EndpointRpcError);
        var roundTrip = serializer.Deserialize(payload, EndpointRpcJsonContext.Default.EndpointRpcError);

        var json = System.Text.Encoding.UTF8.GetString(payload);

        Assert.AreEqual(error, roundTrip);
        Assert.IsFalse(json.Contains("correlationId", StringComparison.Ordinal));
        Assert.IsFalse(json.Contains("occurredAtUtc", StringComparison.Ordinal));
        Assert.IsFalse(json.Contains("isRetryable", StringComparison.Ordinal));
        Assert.IsFalse(json.Contains("validationErrors", StringComparison.Ordinal));

        using var jsonDocument = JsonDocument.Parse(json);
        var hasRecoveryMetadataProperty = false;
        foreach (var property in jsonDocument.RootElement.EnumerateObject())
        {
            if (property.Name.Contains("recovery", StringComparison.OrdinalIgnoreCase))
            {
                hasRecoveryMetadataProperty = true;
                break;
            }
        }

        Assert.IsFalse(
            hasRecoveryMetadataProperty,
            "EndpointRpcError must not serialize recovery metadata properties.");
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
