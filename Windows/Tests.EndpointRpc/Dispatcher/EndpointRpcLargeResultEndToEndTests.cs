using Microsoft.VisualStudio.TestTools.UnitTesting;
using PasswordManagerLocal.Common.Contracts.Responses;
using PasswordManagerLocal.Windows.EndpointRpc.Client;
using PasswordManagerLocal.Windows.EndpointRpc.Contracts;
using PasswordManagerLocal.Windows.EndpointRpc.Contracts.LargeTransfer;
using PasswordManagerLocal.Windows.EndpointRpc.Contracts.Requests;
using PasswordManagerLocal.Windows.EndpointRpc.Contracts.Responses;
using PasswordManagerLocal.Windows.EndpointRpc.Serialization;
using PasswordManagerLocal.Windows.EndpointRpc.Server;
using PasswordManagerLocal.Windows.Tests.EndpointRpc.Infrastructure;
using PasswordManagerLocal.Windows.EndpointRpc.Validation;
using PasswordManagerLocal.Windows.Ipc.Contracts;
using PasswordManagerLocal.Windows.Ipc.Lifecycle;
using PasswordManagerLocal.Windows.Ipc.Protocol;
using PasswordManagerLocal.Windows.Ipc.Serialization;
using PasswordManagerLocal.Windows.Ipc.Server;

namespace PasswordManagerLocal.Windows.Tests.EndpointRpc.Dispatcher;

[TestClass]
public sealed class EndpointRpcLargeResultEndToEndTests
{
    [TestMethod]
    [Timeout(180_000)]
    public async Task MaximumDomainShapedVaultUsesChunkTransferAndInvokesBackendOnce()
    {
        var endpoints = new MaximumSavedPasswordsEndpoints();
        var serializer = new EndpointRpcSerializer();
        var validator = new EndpointRpcContractValidator();
        var codec = new EndpointRpcMessageCodec(serializer);
        await using var dispatcher = new EndpointRpcDispatcher(
            new FixedEndpointRpcEndpointAdapter(endpoints),
            serializer,
            validator,
            new EndpointRpcBackendErrorMapper());
        var handler = new EndpointRpcWindowsIpcRequestHandler(
            dispatcher,
            codec,
            validator,
            new ControllableEndpointRpcAdmissionPolicy());
        await using var transport = new InMemoryEndpointRpcTransport(handler, codec);
        await using var proxy = new NamedPipeEndpointsProxy(transport, serializer, validator);

        var actual = await proxy.GetSavedPasswordsAsync(EndpointRpcTestData.Token);

        Assert.AreEqual(1, endpoints.InvocationCount);
        Assert.AreEqual(1, transport.PublicRequestCount);
        Assert.IsTrue(transport.ChunkRequestCount > 1);
        Assert.AreEqual(1, transport.ReleaseRequestCount);
        Assert.AreEqual(0, dispatcher.LargeResultTransferStore.Count);
        AssertEquivalent(endpoints.ExpectedResponse, actual);
    }

    [TestMethod]
    [Timeout(30_000)]
    public async Task MalformedOwnedChunkRequestInvalidatesServerTransfer()
    {
        var serializer = new EndpointRpcSerializer();
        var validator = new EndpointRpcContractValidator();
        var codec = new EndpointRpcMessageCodec(serializer);
        await using var dispatcher = new EndpointRpcDispatcher(
            new FixedEndpointRpcEndpointAdapter(new OversizedSavedPasswordsEndpoints()),
            serializer,
            validator,
            new EndpointRpcBackendErrorMapper());
        var handler = new EndpointRpcWindowsIpcRequestHandler(
            dispatcher,
            codec,
            validator,
            new ControllableEndpointRpcAdmissionPolicy());
        var connection = new IpcConnectionContext(
            Guid.NewGuid(),
            IpcPeerRole.Ui,
            1234,
            0,
            Guid.NewGuid(),
            IpcCapabilities.EndpointRpc);
        var publicRequestPayload = serializer.Serialize(
            new GetSavedPasswordsEndpointRequest { Token = EndpointRpcTestData.Token },
            EndpointRpcJsonContext.Default.GetSavedPasswordsEndpointRequest);
        var publicResponse = await handler.HandleAsync(
            CreateContext(connection, 201, codec.EncodeRequest(
                EndpointOperationId.GetSavedPasswords,
                publicRequestPayload)),
            CancellationToken.None);
        var descriptor = codec.DecodeResponse(publicResponse.Result!, 201).LargeResult!;
        Assert.AreEqual(1, dispatcher.LargeResultTransferStore.Count);

        var malformedChunkPayload = serializer.Serialize(
            new GetEndpointLargeResultChunkRequest
            {
                TransferId = descriptor.TransferId,
                OriginalCorrelationId = descriptor.OriginalCorrelationId,
                ChunkIndex = 1
            },
            EndpointRpcJsonContext.Default.GetEndpointLargeResultChunkRequest);
        var malformedResponse = await handler.HandleAsync(
            CreateContext(connection, 202, codec.EncodeLargeResultChunkRequest(malformedChunkPayload)),
            CancellationToken.None);

        var failureResponse = codec.DecodeResponse(malformedResponse.Result!, 202);
        Assert.AreEqual(EndpointRpcResponseKind.Failure, failureResponse.ResponseKind);
        Assert.AreEqual(EndpointRpcErrorCode.ValidationFailed, failureResponse.Error!.ErrorCode);
        var exception = Assert.ThrowsExactly<EndpointRpcRemoteException>(() =>
            EndpointRpcClientResponseMapper.RequireSuccess(failureResponse));
        Assert.AreSame(failureResponse.Error, exception.Error);
        Assert.AreEqual(0, dispatcher.LargeResultTransferStore.Count);
        Array.Clear(publicRequestPayload);
        Array.Clear(malformedChunkPayload);
    }

    [TestMethod]
    [Timeout(30_000)]
    public async Task InterruptedChunkTransferReleasesServerStateAndClearsClientPayloads()
    {
        var serializer = new EndpointRpcSerializer();
        var logicalResponse = CreateChunkedResponse();
        var serialized = serializer.Serialize(
            new GetSavedPasswordsEndpointResponse { Passwords = logicalResponse },
            EndpointRpcJsonContext.Default.GetSavedPasswordsEndpointResponse);
        using var cancellationSource = new CancellationTokenSource();
        var descriptor = CreateDescriptor(serialized.Length, chunkSize: 256 * 1024);
        var transport = new ScriptedLargeResultTransport(
            descriptor,
            request =>
            {
                var response = CreateChunkResponse(descriptor, serialized, request.ChunkIndex);
                if (request.ChunkIndex == 0)
                    cancellationSource.Cancel();
                return response;
            });
        await using var proxy = new NamedPipeEndpointsProxy(
            transport,
            serializer,
            new EndpointRpcContractValidator());

        try
        {
            await Assert.ThrowsExactlyAsync<OperationCanceledException>(() =>
                proxy.GetSavedPasswordsAsync(
                    EndpointRpcTestData.Token,
                    cancellationSource.Token));

            Assert.AreEqual(1, transport.PublicRequestCount);
            Assert.AreEqual(1, transport.ChunkRequestCount);
            Assert.AreEqual(1, transport.ReleaseRequestCount);
            Assert.IsTrue(transport.ReturnedPayloads.All(payload => payload.All(value => value == 0)));
        }
        finally
        {
            Array.Clear(serialized);
        }
    }

    private static IpcRequestContext CreateContext(
        IpcConnectionContext connection,
        long correlationId,
        byte[] payload) =>
        new(
            connection,
            new IpcRequestEnvelope(
                correlationId,
                IpcOperationId.EndpointRpcRequest,
                payload),
            new WindowsIpcSerializer());

    private static SavedPasswordsResponse CreateChunkedResponse()
    {
        var description = new string('€', 1000);
        return new SavedPasswordsResponse
        {
            Passwords = Enumerable.Range(1, 400)
                .Select(index => new PasswordInfoResponse
                {
                    Id = Guid.NewGuid(),
                    Name = $"Entry{index}",
                    Description = description,
                    Color = "#FF14B8A6",
                    TagIds = [],
                    CreatedAt = DateTime.SpecifyKind(new DateTime(2026, 1, 1), DateTimeKind.Utc),
                    LastUpdatedAt = DateTime.SpecifyKind(new DateTime(2026, 1, 2), DateTimeKind.Utc)
                })
                .ToArray()
        };
    }

    private static EndpointLargeResultDescriptor CreateDescriptor(int totalLength, int chunkSize) =>
        new()
        {
            TransferId = Guid.NewGuid(),
            OriginalCorrelationId = 91,
            DeclaredTotalLength = totalLength,
            DeclaredChunkCount = checked((totalLength + chunkSize - 1) / chunkSize),
            ChunkSize = chunkSize,
            ExpiresAtUtc = DateTimeOffset.UtcNow.AddMinutes(1)
        };

    private static GetEndpointLargeResultChunkResponse CreateChunkResponse(
        EndpointLargeResultDescriptor descriptor,
        byte[] payload,
        int chunkIndex)
    {
        var offset = checked(chunkIndex * descriptor.ChunkSize);
        var length = Math.Min(descriptor.ChunkSize, payload.Length - offset);
        return new GetEndpointLargeResultChunkResponse
        {
            TransferId = descriptor.TransferId,
            OriginalCorrelationId = descriptor.OriginalCorrelationId,
            ChunkIndex = chunkIndex,
            IsFinal = chunkIndex == descriptor.DeclaredChunkCount - 1,
            DeclaredTotalLength = descriptor.DeclaredTotalLength,
            DeclaredChunkCount = descriptor.DeclaredChunkCount,
            ChunkPayload = payload.AsSpan(offset, length).ToArray()
        };
    }

    private static void AssertEquivalent(
        SavedPasswordsResponse expected,
        SavedPasswordsResponse actual)
    {
        Assert.AreEqual(expected.Passwords.Count, actual.Passwords.Count);
        Assert.AreEqual(expected.Tags.Count, actual.Tags.Count);
        Assert.AreEqual(expected.CustomColors.Count, actual.CustomColors.Count);

        for (var index = 0; index < expected.Passwords.Count; index++)
        {
            var expectedPassword = expected.Passwords[index];
            var actualPassword = actual.Passwords[index];
            Assert.AreEqual(expectedPassword.Id, actualPassword.Id);
            Assert.AreEqual(expectedPassword.Name, actualPassword.Name);
            Assert.AreEqual(expectedPassword.Description, actualPassword.Description);
            Assert.AreEqual(expectedPassword.Color, actualPassword.Color);
            Assert.IsTrue(expectedPassword.TagIds.SequenceEqual(actualPassword.TagIds));
            Assert.AreEqual(expectedPassword.CreatedAt, actualPassword.CreatedAt);
            Assert.AreEqual(expectedPassword.LastUpdatedAt, actualPassword.LastUpdatedAt);
        }

        for (var index = 0; index < expected.Tags.Count; index++)
        {
            Assert.AreEqual(expected.Tags[index].Id, actual.Tags[index].Id);
            Assert.AreEqual(expected.Tags[index].Name, actual.Tags[index].Name);
            Assert.AreEqual(expected.Tags[index].Color, actual.Tags[index].Color);
            Assert.AreEqual(expected.Tags[index].LastUpdatedAt, actual.Tags[index].LastUpdatedAt);
        }

        for (var index = 0; index < expected.CustomColors.Count; index++)
        {
            Assert.AreEqual(expected.CustomColors[index].Id, actual.CustomColors[index].Id);
            Assert.AreEqual(expected.CustomColors[index].ColorName, actual.CustomColors[index].ColorName);
            Assert.AreEqual(expected.CustomColors[index].ColorCode, actual.CustomColors[index].ColorCode);
            Assert.AreEqual(expected.CustomColors[index].LastUpdatedAt, actual.CustomColors[index].LastUpdatedAt);
        }
    }
}
