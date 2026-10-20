using Microsoft.VisualStudio.TestTools.UnitTesting;
using PasswordManagerLocal.Windows.EndpointRpc.Client;
using PasswordManagerLocal.Windows.EndpointRpc.Contracts;
using PasswordManagerLocal.Windows.EndpointRpc.Contracts.LargeTransfer;
using PasswordManagerLocal.Windows.EndpointRpc.Serialization;
using PasswordManagerLocal.Windows.Tests.EndpointRpc.Infrastructure;
using PasswordManagerLocal.Windows.EndpointRpc.Validation;

namespace PasswordManagerLocal.Windows.Tests.EndpointRpc.Client;

[TestClass]
public sealed class EndpointRpcCorrelationBindingTests
{
    [TestMethod]
    public void InlineEndpointErrorDoesNotDuplicateOuterTransportCorrelation()
    {
        var codec = CreateCodec();
        var encoded = codec.EncodeFailure(CreateError());

        var response = codec.DecodeResponse(encoded, 41);

        Assert.AreEqual(EndpointRpcResponseKind.Failure, response.ResponseKind);
        Assert.AreEqual(EndpointRpcErrorCode.Conflict, response.Error!.ErrorCode);
        Assert.AreEqual(EndpointFailureKind.Generic, response.Error.FailureKind);
        Assert.IsNull(typeof(EndpointRpcError).GetProperty("CorrelationId"));
        var exception = Assert.ThrowsExactly<EndpointRpcRemoteException>(() =>
            EndpointRpcClientResponseMapper.RequireSuccess(response));
        Assert.AreSame(response.Error, exception.Error);
        Array.Clear(encoded);
    }

    [TestMethod]
    public void LargeResultDescriptorWithMatchingOuterCorrelationIsAccepted()
    {
        var codec = CreateCodec();
        var descriptor = CreateDescriptor(41);
        var encoded = codec.EncodeLargeResult(descriptor);

        var response = codec.DecodeResponse(encoded, 41);

        Assert.AreEqual(EndpointRpcResponseKind.LargeResult, response.ResponseKind);
        Assert.AreEqual(41, response.LargeResult!.OriginalCorrelationId);
        Array.Clear(encoded);
    }

    [TestMethod]
    public void LargeResultDescriptorWithDifferentOuterCorrelationIsRejected()
    {
        var codec = CreateCodec();
        var encoded = codec.EncodeLargeResult(CreateDescriptor(42));

        var exception = Assert.ThrowsExactly<EndpointRpcCorrelationMismatchException>(() =>
            codec.DecodeResponse(encoded, 41));

        Assert.AreEqual(EndpointRpcErrorCode.EndpointCorrelationMismatch, exception.ErrorCode);
        Assert.IsFalse(exception.RequiresProcessRestart);
        Assert.IsFalse(exception.Message.Contains("41", StringComparison.Ordinal));
        Assert.IsFalse(exception.Message.Contains("42", StringComparison.Ordinal));
        Array.Clear(encoded);
    }

    [TestMethod]
    public async Task ReadOnlyMismatchedDescriptorIsRejectedBeforeChunksOrRelease()
    {
        await using var transport = new ScriptedEndpointRpcResponseTransport(
            correlationId => EncodeDescriptor(correlationId + 1));
        await using var proxy = CreateProxy(transport);

        await Assert.ThrowsExactlyAsync<EndpointRpcCorrelationMismatchException>(() =>
            proxy.GetSavedPasswordsAsync(EndpointRpcTestData.Token));

        Assert.AreEqual(1, transport.PublicRequestCount);
        Assert.AreEqual(0, transport.ChunkRequestCount);
        Assert.AreEqual(0, transport.ReleaseRequestCount);
    }

    [TestMethod]
    public async Task ReadOnlyMatchingDescriptorStartsChunkTransfer()
    {
        await using var transport = new ScriptedEndpointRpcResponseTransport(EncodeDescriptor);
        await using var proxy = CreateProxy(transport);

        await Assert.ThrowsExactlyAsync<InvalidOperationException>(() =>
            proxy.GetSavedPasswordsAsync(EndpointRpcTestData.Token));

        Assert.AreEqual(1, transport.ChunkRequestCount);
        Assert.AreEqual(1, transport.ReleaseRequestCount);
    }

    [TestMethod]
    public async Task SentMutationWithMismatchedDescriptorReportsOutcomeUnknownWithoutChunks()
    {
        await using var transport = new ScriptedEndpointRpcResponseTransport(
            correlationId => EncodeDescriptor(correlationId + 1));
        await using var proxy = CreateProxy(transport);

        var exception = await Assert.ThrowsExactlyAsync<EndpointOperationOutcomeUnknownException>(() =>
            proxy.LogoutAsync(EndpointRpcTestData.Token));

        Assert.IsFalse(exception.RequiresProcessRestart);
        Assert.AreEqual(0, transport.ChunkRequestCount);
        Assert.AreEqual(0, transport.ReleaseRequestCount);
    }

    private static NamedPipeEndpointsProxy CreateProxy(IEndpointRpcTransport transport) =>
        new(transport, new EndpointRpcSerializer(), new EndpointRpcContractValidator());

    private static EndpointRpcMessageCodec CreateCodec() =>
        new(new EndpointRpcSerializer());

    private static byte[] EncodeDescriptor(long correlationId) =>
        CreateCodec().EncodeLargeResult(CreateDescriptor(correlationId));

    private static EndpointRpcError CreateError() =>
        new(
            EndpointRpcErrorCode.Conflict,
            EndpointFailureKind.Generic,
            "The endpoint operation conflicts with current state.",
            RequiresProcessRestart: false,
            EndpointMutationOutcome.NotApplicable);

    private static EndpointLargeResultDescriptor CreateDescriptor(long correlationId) =>
        new()
        {
            TransferId = Guid.NewGuid(),
            OriginalCorrelationId = correlationId,
            DeclaredTotalLength = 8,
            DeclaredChunkCount = 2,
            ChunkSize = 4,
            ExpiresAtUtc = DateTimeOffset.UtcNow.AddMinutes(1)
        };
}
