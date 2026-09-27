using Microsoft.VisualStudio.TestTools.UnitTesting;
using PasswordManagerLocal.Windows.EndpointRpc.Contracts;
using PasswordManagerLocal.Windows.EndpointRpc.Contracts.LargeTransfer;
using PasswordManagerLocal.Windows.EndpointRpc.Serialization;
using PasswordManagerLocal.Windows.EndpointRpc.Server;
using PasswordManagerLocal.Windows.EndpointRpc.Server.LargeTransfer;
using PasswordManagerLocal.Windows.Tests.EndpointRpc.Infrastructure;
using PasswordManagerLocal.Windows.Ipc.Lifecycle;
using PasswordManagerLocal.Windows.Ipc.Protocol;

namespace PasswordManagerLocal.Windows.Tests.EndpointRpc.LargeTransfer;

[TestClass]
public sealed class EndpointLargeResultTransferStoreTests
{
    [TestMethod]
    public async Task SuccessfulTransferReleaseIsIdempotentAndClearsServerBuffer()
    {
        await using var store = new EndpointLargeResultTransferStore();
        var context = CreateContext(1, 51);
        var payload = Enumerable.Repeat((byte)0x5A, EndpointRpcLimits.MaximumLargeResultChunkBytes + 3).ToArray();
        var descriptor = store.Create(context, payload);

        var first = store.GetChunk(
            context.ConnectionId,
            context.PeerSessionId,
            CreateChunkRequest(descriptor, 0));
        var second = store.GetChunk(
            context.ConnectionId,
            context.PeerSessionId,
            CreateChunkRequest(descriptor, 1));
        var released = store.Release(
            context.ConnectionId,
            context.PeerSessionId,
            CreateReleaseRequest(descriptor));
        var repeated = store.Release(
            context.ConnectionId,
            context.PeerSessionId,
            CreateReleaseRequest(descriptor));

        Assert.AreEqual(EndpointRpcLimits.MaximumLargeResultChunkBytes, first.ChunkPayload.Length);
        Assert.AreEqual(3, second.ChunkPayload.Length);
        Assert.IsTrue(second.IsFinal);
        Assert.IsTrue(released);
        Assert.IsFalse(repeated);
        Assert.AreEqual(0, store.Count);
        Assert.IsTrue(payload.All(value => value == 0));
        Array.Clear(first.ChunkPayload);
        Array.Clear(second.ChunkPayload);
    }

    [TestMethod]
    public async Task DuplicateMissingAndOutOfOrderChunksInvalidateAndClearTransfer()
    {
        await using var store = new EndpointLargeResultTransferStore();
        var context = CreateContext(2, 52);
        var payload = Enumerable.Repeat((byte)0x4A, 16).ToArray();
        var descriptor = store.Create(context, payload);

        Assert.ThrowsExactly<EndpointRpcPayloadException>(() =>
            store.GetChunk(
                context.ConnectionId,
                context.PeerSessionId,
                CreateChunkRequest(descriptor, 1)));

        Assert.AreEqual(0, store.Count);
        Assert.IsTrue(payload.All(value => value == 0));
    }

    [TestMethod]
    public async Task WrongConnectionOrSessionCannotReadAnotherOwnerTransfer()
    {
        await using var store = new EndpointLargeResultTransferStore();
        var context = CreateContext(3, 53);
        var payload = Enumerable.Repeat((byte)0x3A, 16).ToArray();
        var descriptor = store.Create(context, payload);

        Assert.ThrowsExactly<EndpointRpcPayloadException>(() =>
            store.GetChunk(
                Guid.NewGuid(),
                context.PeerSessionId,
                CreateChunkRequest(descriptor, 0)));
        Assert.ThrowsExactly<EndpointRpcPayloadException>(() =>
            store.GetChunk(
                context.ConnectionId,
                Guid.NewGuid(),
                CreateChunkRequest(descriptor, 0)));

        Assert.AreEqual(1, store.Count);
        Assert.IsFalse(payload.All(value => value == 0));
        Assert.IsTrue(store.Release(
            context.ConnectionId,
            context.PeerSessionId,
            CreateReleaseRequest(descriptor)));
        Assert.IsTrue(payload.All(value => value == 0));
    }

    [TestMethod]
    public async Task DifferentOriginalRequestCannotReadTransferOnSameConnection()
    {
        await using var store = new EndpointLargeResultTransferStore();
        var context = CreateContext(12, 63);
        var payload = Enumerable.Repeat((byte)0x39, 16).ToArray();
        var descriptor = store.Create(context, payload);
        var wrongRequest = CreateChunkRequest(descriptor, 0);
        wrongRequest.OriginalCorrelationId++;

        Assert.ThrowsExactly<EndpointRpcPayloadException>(() =>
            store.GetChunk(
                context.ConnectionId,
                context.PeerSessionId,
                wrongRequest));

        Assert.AreEqual(1, store.Count);
        Assert.IsTrue(store.Release(
            context.ConnectionId,
            context.PeerSessionId,
            CreateReleaseRequest(descriptor)));
        Assert.IsTrue(payload.All(value => value == 0));
    }

    [TestMethod]
    public async Task PerConnectionAndGlobalTransferCapacityAreBoundedAndReusable()
    {
        await using var store = new EndpointLargeResultTransferStore();
        var firstContext = CreateContext(4, 54);
        var secondContext = CreateContext(5, 55);
        var thirdContext = CreateContext(6, 56);
        var firstPayload = Enumerable.Repeat((byte)0x11, 16).ToArray();
        var secondPayload = Enumerable.Repeat((byte)0x22, 16).ToArray();
        var thirdPayload = Enumerable.Repeat((byte)0x33, 16).ToArray();
        var sameConnectionPayload = Enumerable.Repeat((byte)0x44, 16).ToArray();
        var first = store.Create(firstContext, firstPayload);
        var second = store.Create(secondContext, secondPayload);

        Assert.ThrowsExactly<EndpointLargeResultCapacityException>(() =>
            store.Create(firstContext with { CorrelationId = 57 }, sameConnectionPayload));
        Assert.ThrowsExactly<EndpointLargeResultCapacityException>(() =>
            store.Create(thirdContext, thirdPayload));
        Assert.AreEqual(EndpointRpcLimits.MaximumConcurrentLargeTransfers, store.Count);
        Assert.IsTrue(thirdPayload.All(value => value == 0x33));
        Assert.IsTrue(sameConnectionPayload.All(value => value == 0x44));

        Assert.IsTrue(store.Release(
            firstContext.ConnectionId,
            firstContext.PeerSessionId,
            CreateReleaseRequest(first)));
        var third = store.Create(thirdContext, thirdPayload);

        Assert.AreEqual(EndpointRpcLimits.MaximumConcurrentLargeTransfers, store.Count);
        Assert.IsTrue(firstPayload.All(value => value == 0));
        Assert.IsTrue(store.Release(
            secondContext.ConnectionId,
            secondContext.PeerSessionId,
            CreateReleaseRequest(second)));
        Assert.IsTrue(store.Release(
            thirdContext.ConnectionId,
            thirdContext.PeerSessionId,
            CreateReleaseRequest(third)));
        Assert.IsTrue(secondPayload.All(value => value == 0));
        Assert.IsTrue(thirdPayload.All(value => value == 0));
        Assert.IsTrue(sameConnectionPayload.All(value => value == 0x44));
    }

    [TestMethod]
    public async Task DisconnectAndSessionShutdownRemoveOwnedTransfersAndClearBuffers()
    {
        await using var store = new EndpointLargeResultTransferStore();
        var context = CreateContext(7, 58);
        var payload = Enumerable.Repeat((byte)0x2A, 16).ToArray();
        store.Create(context, payload);

        await store.OnConnectionLifecycleChangedAsync(
            new IpcConnectionLifecycleNotification(
                context.ConnectionId,
                IpcConnectionLifecycleState.Disconnected,
                Context: null,
                IpcDisconnectKind.Clean,
                DateTimeOffset.UtcNow));

        Assert.AreEqual(0, store.Count);
        Assert.IsTrue(payload.All(value => value == 0));
    }

    [TestMethod]
    public async Task ExpiredTransferIsRemovedAndCapacityIsReleased()
    {
        var timeProvider = new AdjustableTimeProvider(
            new DateTimeOffset(2026, 7, 22, 12, 0, 0, TimeSpan.Zero));
        await using var store = new EndpointLargeResultTransferStore(timeProvider);
        var firstContext = CreateContext(8, 59);
        var secondContext = CreateContext(9, 60);
        var firstPayload = Enumerable.Repeat((byte)0x1A, 16).ToArray();
        var secondPayload = new byte[16];
        store.Create(firstContext, firstPayload);

        timeProvider.Advance(TimeSpan.FromSeconds(
            EndpointRpcLimits.LargeResultTransferLifetimeSeconds + 1));
        Assert.AreEqual(1, store.SweepExpired());
        var second = store.Create(secondContext, secondPayload);

        Assert.IsTrue(firstPayload.All(value => value == 0));
        Assert.AreEqual(1, store.Count);
        Assert.IsTrue(store.Release(
            secondContext.ConnectionId,
            secondContext.PeerSessionId,
            CreateReleaseRequest(second)));
    }

    [TestMethod]
    public async Task WrongOwnerReleaseStillClearsExpiredTransfersAndReleasesCapacity()
    {
        var timeProvider = new AdjustableTimeProvider(
            new DateTimeOffset(2026, 7, 22, 12, 0, 0, TimeSpan.Zero));
        await using var store = new EndpointLargeResultTransferStore(timeProvider);
        var expiredContext = CreateContext(10, 61);
        var activeContext = CreateContext(11, 62);
        var expiredPayload = Enumerable.Repeat((byte)0x7A, 16).ToArray();
        var activePayload = Enumerable.Repeat((byte)0x7B, 16).ToArray();
        store.Create(expiredContext, expiredPayload);
        timeProvider.Advance(TimeSpan.FromSeconds(
            EndpointRpcLimits.LargeResultTransferLifetimeSeconds - 1));
        var active = store.Create(activeContext, activePayload);
        timeProvider.Advance(TimeSpan.FromSeconds(2));

        Assert.ThrowsExactly<EndpointRpcPayloadException>(() =>
            store.Release(
                Guid.NewGuid(),
                activeContext.PeerSessionId,
                CreateReleaseRequest(active)));

        Assert.IsTrue(expiredPayload.All(value => value == 0));
        Assert.AreEqual(1, store.Count);
        Assert.IsTrue(store.Release(
            activeContext.ConnectionId,
            activeContext.PeerSessionId,
            CreateReleaseRequest(active)));
        Assert.IsTrue(activePayload.All(value => value == 0));
    }

    [TestMethod]
    public async Task ValidationFailureCanInvalidateOnlyTheOwnedTransfer()
    {
        await using var store = new EndpointLargeResultTransferStore();
        var context = CreateContext(10, 61);
        var payload = Enumerable.Repeat((byte)0x6A, 16).ToArray();
        var descriptor = store.Create(context, payload);

        Assert.IsFalse(store.InvalidateIfOwned(
            Guid.NewGuid(),
            context.PeerSessionId,
            descriptor.TransferId,
            descriptor.OriginalCorrelationId));
        Assert.AreEqual(1, store.Count);
        Assert.IsTrue(store.InvalidateIfOwned(
            context.ConnectionId,
            context.PeerSessionId,
            descriptor.TransferId,
            descriptor.OriginalCorrelationId));

        Assert.AreEqual(0, store.Count);
        Assert.IsTrue(payload.All(value => value == 0));
    }

    private static EndpointRequestContext CreateContext(int seed, long correlationId) =>
        new(
            CreateGuid(seed),
            correlationId,
            EndpointOperationId.GetSavedPasswords,
            IpcPeerRole.Ui,
            1234,
            CreateGuid(seed + 100),
            CancellationToken.None);

    private static GetEndpointLargeResultChunkRequest CreateChunkRequest(
        EndpointLargeResultDescriptor descriptor,
        int chunkIndex) =>
        new()
        {
            TransferId = descriptor.TransferId,
            OriginalCorrelationId = descriptor.OriginalCorrelationId,
            ChunkIndex = chunkIndex
        };

    private static ReleaseEndpointLargeResultRequest CreateReleaseRequest(
        EndpointLargeResultDescriptor descriptor) =>
        new()
        {
            TransferId = descriptor.TransferId,
            OriginalCorrelationId = descriptor.OriginalCorrelationId
        };

    private static Guid CreateGuid(int seed)
    {
        Span<byte> bytes = stackalloc byte[16];
        BitConverter.TryWriteBytes(bytes, seed);
        bytes[15] = 1;
        return new Guid(bytes);
    }
}
