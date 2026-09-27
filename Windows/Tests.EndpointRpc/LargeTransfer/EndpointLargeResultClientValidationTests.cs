using Microsoft.VisualStudio.TestTools.UnitTesting;
using PasswordManagerLocal.Windows.EndpointRpc.Client;
using PasswordManagerLocal.Windows.EndpointRpc.Contracts;
using PasswordManagerLocal.Windows.EndpointRpc.Contracts.LargeTransfer;
using PasswordManagerLocal.Windows.EndpointRpc.Serialization;
using PasswordManagerLocal.Windows.Tests.EndpointRpc.Infrastructure;
using PasswordManagerLocal.Windows.EndpointRpc.Validation;

namespace PasswordManagerLocal.Windows.Tests.EndpointRpc.LargeTransfer;

[TestClass]
public sealed class EndpointLargeResultClientValidationTests
{
    [TestMethod]
    public async Task WrongTransferIdIsRejectedAndReleased()
    {
        await AssertRejectedAsync((descriptor, request) =>
        {
            var response = CreateChunk(descriptor, request.ChunkIndex);
            response.TransferId = Guid.NewGuid();
            return response;
        });
    }

    [TestMethod]
    public async Task WrongOriginalCorrelationIsRejectedAndReleased()
    {
        await AssertRejectedAsync((descriptor, request) =>
        {
            var response = CreateChunk(descriptor, request.ChunkIndex);
            response.OriginalCorrelationId = descriptor.OriginalCorrelationId + 1;
            return response;
        });
    }

    [TestMethod]
    public async Task PreviousTransferCorrelationCannotBeReusedForNewRequest()
    {
        var descriptor = CreateDescriptor(8, 4);
        var previousCorrelationId = descriptor.OriginalCorrelationId - 1;
        var transport = new ScriptedLargeResultTransport(
            descriptor,
            request =>
            {
                var response = CreateChunk(descriptor, request.ChunkIndex);
                response.OriginalCorrelationId = previousCorrelationId;
                return response;
            });
        await using var proxy = CreateProxy(transport);

        await Assert.ThrowsExactlyAsync<EndpointRpcPayloadException>(() =>
            proxy.GetSavedPasswordsAsync(EndpointRpcTestData.Token));

        Assert.AreEqual(1, transport.ChunkRequestCount);
        Assert.AreEqual(1, transport.ReleaseRequestCount);
    }

    [TestMethod]
    public async Task DuplicateOrOutOfOrderChunkIsRejectedAndReleased()
    {
        await AssertRejectedAsync((descriptor, request) =>
            CreateChunk(descriptor, request.ChunkIndex + 1));
    }

    [TestMethod]
    public async Task IncorrectDeclaredTotalIsRejectedAndReleased()
    {
        await AssertRejectedAsync((descriptor, request) =>
        {
            var response = CreateChunk(descriptor, request.ChunkIndex);
            response.DeclaredTotalLength = descriptor.DeclaredTotalLength + 1;
            return response;
        });
    }

    [TestMethod]
    public async Task IncorrectFinalMarkerIsRejectedAndReleased()
    {
        await AssertRejectedAsync((descriptor, request) =>
        {
            var response = CreateChunk(descriptor, request.ChunkIndex);
            response.IsFinal = true;
            return response;
        });
    }

    [TestMethod]
    public async Task AccumulatedLengthOverflowIsRejectedAndReleased()
    {
        var descriptor = CreateDescriptor(totalLength: 8, chunkSize: 4);
        var transport = new ScriptedLargeResultTransport(
            descriptor,
            request => CreateChunk(
                descriptor,
                request.ChunkIndex,
                payloadLength: request.ChunkIndex == 0 ? 4 : 5));
        await using var proxy = CreateProxy(transport);

        await Assert.ThrowsExactlyAsync<EndpointRpcPayloadException>(() =>
            proxy.GetSavedPasswordsAsync(EndpointRpcTestData.Token));

        Assert.AreEqual(2, transport.ChunkRequestCount);
        Assert.AreEqual(1, transport.ReleaseRequestCount);
        Assert.IsTrue(transport.ReturnedPayloads.All(payload => payload.All(value => value == 0)));
    }

    [TestMethod]
    public async Task OversizedChunkIsRejectedAndReleased()
    {
        var descriptor = CreateDescriptor(
            EndpointRpcLimits.MaximumLargeResultChunkBytes,
            EndpointRpcLimits.MaximumLargeResultChunkBytes);
        var transport = new ScriptedLargeResultTransport(
            descriptor,
            request => CreateChunk(
                descriptor,
                request.ChunkIndex,
                EndpointRpcLimits.MaximumLargeResultChunkBytes + 1));
        await using var proxy = CreateProxy(transport);

        await Assert.ThrowsExactlyAsync<EndpointRpcPayloadException>(() =>
            proxy.GetSavedPasswordsAsync(EndpointRpcTestData.Token));

        Assert.AreEqual(1, transport.ReleaseRequestCount);
    }

    [TestMethod]
    public async Task InvalidDescriptorBoundsAreRejectedBeforeAllocationOrChunkRequest()
    {
        var descriptor = CreateDescriptor(8, 4);
        descriptor.DeclaredChunkCount = EndpointRpcLimits.MaximumLargeResultChunkCount + 1;
        var transport = new ScriptedLargeResultTransport(
            descriptor,
            _ => throw new InvalidOperationException());
        await using var proxy = CreateProxy(transport);

        await Assert.ThrowsExactlyAsync<EndpointRpcPayloadException>(() =>
            proxy.GetSavedPasswordsAsync(EndpointRpcTestData.Token));

        Assert.AreEqual(0, transport.ChunkRequestCount);
        Assert.AreEqual(1, transport.ReleaseRequestCount);
    }

    private static async Task AssertRejectedAsync(
        Func<EndpointLargeResultDescriptor, GetEndpointLargeResultChunkRequest, GetEndpointLargeResultChunkResponse> factory)
    {
        var descriptor = CreateDescriptor(8, 4);
        var transport = new ScriptedLargeResultTransport(
            descriptor,
            request => factory(descriptor, request));
        await using var proxy = CreateProxy(transport);

        await Assert.ThrowsExactlyAsync<EndpointRpcPayloadException>(() =>
            proxy.GetSavedPasswordsAsync(EndpointRpcTestData.Token));

        Assert.AreEqual(1, transport.ReleaseRequestCount);
        Assert.IsTrue(transport.ReturnedPayloads.All(payload => payload.All(value => value == 0)));
    }

    private static NamedPipeEndpointsProxy CreateProxy(IEndpointRpcTransport transport) =>
        new(
            transport,
            new EndpointRpcSerializer(),
            new EndpointRpcContractValidator());

    private static EndpointLargeResultDescriptor CreateDescriptor(int totalLength, int chunkSize) =>
        new()
        {
            TransferId = Guid.NewGuid(),
            OriginalCorrelationId = 41,
            DeclaredTotalLength = totalLength,
            DeclaredChunkCount = checked((totalLength + chunkSize - 1) / chunkSize),
            ChunkSize = chunkSize,
            ExpiresAtUtc = DateTimeOffset.UtcNow.AddMinutes(1)
        };

    private static GetEndpointLargeResultChunkResponse CreateChunk(
        EndpointLargeResultDescriptor descriptor,
        int chunkIndex,
        int? payloadLength = null) =>
        new()
        {
            TransferId = descriptor.TransferId,
            OriginalCorrelationId = descriptor.OriginalCorrelationId,
            ChunkIndex = chunkIndex,
            IsFinal = chunkIndex == descriptor.DeclaredChunkCount - 1,
            DeclaredTotalLength = descriptor.DeclaredTotalLength,
            DeclaredChunkCount = descriptor.DeclaredChunkCount,
            ChunkPayload = new byte[payloadLength ?? descriptor.ChunkSize]
        };
}
