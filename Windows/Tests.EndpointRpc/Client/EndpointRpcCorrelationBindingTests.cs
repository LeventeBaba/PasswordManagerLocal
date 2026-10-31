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
    public void InlineFailureCodecAndResponseMapperPreserveRemoteError()
    {
        var codec = CreateCodec();
        var encoded = codec.EncodeFailure(CreateError());

        var response = EndpointRpcTransportResponse.Failure(codec.DecodeFailure(encoded));

        Assert.AreEqual(EndpointRpcResponseKind.Failure, response.ResponseKind);
        Assert.AreEqual(EndpointRpcErrorCode.Conflict, response.Error!.ErrorCode);
        Assert.AreEqual(EndpointFailureKind.Generic, response.Error.FailureKind);
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

        var response = EndpointRpcTransportResponse.Large(codec.DecodeLargeResult(encoded, 41));

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
            codec.DecodeLargeResult(encoded, 41));

        Assert.AreEqual(EndpointRpcErrorCode.EndpointCorrelationMismatch, exception.ErrorCode);
        Assert.IsFalse(exception.RequiresProcessRestart);
        Assert.IsFalse(exception.Message.Contains("41", StringComparison.Ordinal));
        Assert.IsFalse(exception.Message.Contains("42", StringComparison.Ordinal));
        Array.Clear(encoded);
    }

    [TestMethod]
    [Timeout(10_000)]
    public async Task ReadOnlyTransportRejectsDescriptorForAnotherRequestBeforeTransfer()
    {
        await using var peer = new ScriptedLargeResultIpcConnection(mismatchCorrelation: true);
        await using var transport = await CreateProductionTransportAsync(peer);
        await using var proxy = CreateProxy(transport);

        await Assert.ThrowsExactlyAsync<EndpointRpcCorrelationMismatchException>(() =>
            proxy.GetSavedPasswordsAsync(EndpointRpcTestData.Token));

        Assert.AreEqual(1, peer.PublicRequestCount);
        Assert.AreEqual(0, peer.ChunkRequestCount);
        Assert.AreEqual(0, peer.ReleaseRequestCount);
    }

    [TestMethod]
    [Timeout(10_000)]
    public async Task MatchingDescriptorCompletesLargeResultThroughProductionTransport()
    {
        await using var peer = new ScriptedLargeResultIpcConnection(mismatchCorrelation: false);
        await using var transport = await CreateProductionTransportAsync(peer);
        await using var proxy = CreateProxy(transport);

        var result = await proxy.GetSavedPasswordsAsync(EndpointRpcTestData.Token);

        Assert.IsEmpty(result.Passwords);
        Assert.AreEqual(1, peer.PublicRequestCount);
        Assert.AreEqual(1, peer.ChunkRequestCount);
        Assert.AreEqual(1, peer.ReleaseRequestCount);
    }

    [TestMethod]
    [Timeout(10_000)]
    public async Task SentMutationCorrelationFailureReportsUnknownWithoutStartingTransfer()
    {
        await using var peer = new ScriptedLargeResultIpcConnection(mismatchCorrelation: true);
        await using var transport = await CreateProductionTransportAsync(peer);
        await using var proxy = CreateProxy(transport);

        var exception = await Assert.ThrowsExactlyAsync<EndpointOperationOutcomeUnknownException>(() =>
            proxy.LogoutAsync(EndpointRpcTestData.Token));

        var transportFailure = Assert.IsInstanceOfType<EndpointRpcTransportException>(exception.InnerException);
        Assert.IsInstanceOfType<EndpointRpcCorrelationMismatchException>(transportFailure.InnerException);
        Assert.IsFalse(exception.RequiresProcessRestart);
        Assert.AreEqual(1, peer.PublicRequestCount);
        Assert.AreEqual(0, peer.ChunkRequestCount);
        Assert.AreEqual(0, peer.ReleaseRequestCount);
    }

    private static async Task<WindowsEndpointRpcTransport> CreateProductionTransportAsync(ScriptedLargeResultIpcConnection peer)
    {
        var client = new PasswordManagerLocal.Windows.Ipc.Client.WindowsIpcClient(
            peer, new WindowsIpcSerializer(),
            new PasswordManagerLocal.Windows.Ipc.Client.WindowsIpcClientOptions(
                IpcPeerRole.Ui, IpcPeerRole.Agent, IpcCapabilities.EndpointRpc,
                Environment.ProcessId, 0, Guid.NewGuid(), requiredServerCapabilities: IpcCapabilities.EndpointRpc));
        try
        {
            await client.HandshakeAsync();
            return new WindowsEndpointRpcTransport(client, CreateCodec());
        }
        catch
        {
            await client.DisposeAsync();
            throw;
        }
    }

    private static NamedPipeEndpointsProxy CreateProxy(IEndpointRpcTransport transport) =>
        new(transport, new EndpointRpcSerializer(), new EndpointRpcContractValidator());

    private static EndpointRpcMessageCodec CreateCodec() =>
        new(new EndpointRpcSerializer());

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
