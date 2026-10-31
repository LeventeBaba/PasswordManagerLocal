using Microsoft.VisualStudio.TestTools.UnitTesting;
using PasswordManagerLocal.Windows.EndpointRpc.Contracts;
using PasswordManagerLocal.Windows.EndpointRpc.Contracts.LargeTransfer;
using PasswordManagerLocal.Windows.EndpointRpc.Serialization;
using System.Text.Json;

namespace PasswordManagerLocal.Windows.Tests.EndpointRpc.Serialization;

[TestClass]
public sealed class EndpointRpcSerializationTests
{

    [TestMethod]
    [DataRow("{", true)]
    [DataRow("{\"token\":\"00000000-0000-0000-0000-000000000001\",\"unexpected\":true}", false)]
    public void MalformedOrUnknownMemberRequestJsonIsRejected(string json, bool loginContract)
    {
        var serializer = new EndpointRpcSerializer();
        var payload = System.Text.Encoding.UTF8.GetBytes(json);

        Assert.ThrowsExactly<EndpointRpcPayloadException>(() =>
        {
            if (loginContract)
                serializer.Deserialize(payload, EndpointRpcJsonContext.Default.LoginEndpointRequest);
            else
                serializer.Deserialize(payload, EndpointRpcJsonContext.Default.GetSavedPasswordsEndpointRequest);
        });
    }

    [TestMethod]
    public void PopulatedLargeTransferContractsPreserveAllValues()
    {
        var id = Guid.Parse("33333333-3333-3333-3333-333333333333");
        AssertPopulatedRoundTrip(new EndpointLargeResultDescriptor
        {
            TransferId = id, OriginalCorrelationId = 41, DeclaredTotalLength = 4,
            DeclaredChunkCount = 2, ChunkSize = 2,
            ExpiresAtUtc = new DateTimeOffset(2030, 1, 2, 3, 4, 5, TimeSpan.Zero)
        }, EndpointRpcJsonContext.Default.EndpointLargeResultDescriptor);
        AssertPopulatedRoundTrip(new GetEndpointLargeResultChunkRequest
        {
            TransferId = id, OriginalCorrelationId = 41, ChunkIndex = 1
        }, EndpointRpcJsonContext.Default.GetEndpointLargeResultChunkRequest);
        AssertPopulatedRoundTrip(new GetEndpointLargeResultChunkResponse
        {
            TransferId = id, OriginalCorrelationId = 41, ChunkIndex = 1, IsFinal = true,
            DeclaredTotalLength = 4, DeclaredChunkCount = 2, ChunkPayload = [0x12, 0x34]
        }, EndpointRpcJsonContext.Default.GetEndpointLargeResultChunkResponse);
        AssertPopulatedRoundTrip(new ReleaseEndpointLargeResultRequest
        {
            TransferId = id, OriginalCorrelationId = 41
        }, EndpointRpcJsonContext.Default.ReleaseEndpointLargeResultRequest);
        AssertPopulatedRoundTrip(new ReleaseEndpointLargeResultResponse { Released = true },
            EndpointRpcJsonContext.Default.ReleaseEndpointLargeResultResponse);
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public void PartialCommitErrorRoundTripsWithOnlyAllowlistedWireFields(bool restartRequired)
    {
        var serializer = new EndpointRpcSerializer();
        var error = new EndpointRpcError(EndpointRpcErrorCode.OperationPartiallyCommitted,
            EndpointFailureKind.DeviceEnrollment,
            "The device addition was committed, but enrollment recovery is required.", restartRequired,
            EndpointMutationOutcome.PartiallyCommittedRecoveryRequired,
            DeviceEnrollmentErrorCode.NewDeviceConnectionFailed);
        var payload = serializer.Serialize(error, EndpointRpcJsonContext.Default.EndpointRpcError);
        try
        {
            var restored = serializer.Deserialize(payload, EndpointRpcJsonContext.Default.EndpointRpcError);
            using var json = JsonDocument.Parse(payload);

            Assert.AreEqual(error, restored);
            CollectionAssert.AreEquivalent(new[]
            {
                "errorCode", "failureKind", "safeMessage", "requiresProcessRestart", "mutationOutcome", "enrollmentErrorCode"
            }, json.RootElement.EnumerateObject().Select(property => property.Name).ToArray());
        }
        finally
        {
            Array.Clear(payload);
        }
    }

    private static void AssertPopulatedRoundTrip<T>(
        T expected, System.Text.Json.Serialization.Metadata.JsonTypeInfo<T> metadata)
    {
        var serializer = new EndpointRpcSerializer();
        var payload = serializer.Serialize(expected, metadata);
        try
        {
            var actual = serializer.Deserialize(payload, metadata);
            foreach (var property in typeof(T).GetProperties())
            {
                var before = property.GetValue(expected);
                var after = property.GetValue(actual);
                var label = typeof(T).Name + "." + property.Name;
                if (before is byte[] bytes)
                    CollectionAssert.AreEqual(bytes, Assert.IsInstanceOfType<byte[]>(after), label);
                else
                    Assert.AreEqual(before, after, label);
            }
        }
        finally
        {
            Array.Clear(payload);
        }
    }
}
