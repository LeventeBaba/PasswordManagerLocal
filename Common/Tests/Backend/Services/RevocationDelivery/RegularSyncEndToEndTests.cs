using Microsoft.Extensions.DependencyInjection;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using PasswordManagerLocal.Common.Backend.Abstractions.Repositories;
using PasswordManagerLocal.Common.Backend.Abstractions.Services;
using PasswordManagerLocal.Common.Backend.Models;
using PasswordManagerLocal.Common.Backend.Sync;
using PasswordManagerLocal.Common.Backend.Sync.Tcp;
using Google.Protobuf;
using PasswordManagerLocal.Common.Tests.TestInfrastructure;
using System.Text;

namespace PasswordManagerLocal.Common.Tests.Backend.Services;

public sealed partial class RegularSyncEndToEndTests
{
    [TestMethod]
    [Timeout(TestTimeoutMilliseconds)]
    [TestCategory("Backend")]
    [TestCategory("Integration")]
    public async Task IdleTcpListener_ServesHistoricalControlInventory_AndDeclinesOrdinarySnapshotTraffic()
    {
        await using var pair = await CreateEnrolledPairAsync();
        await PauseAutomaticDeliveryAsync(pair);
        await pair.Target.Identity.SetSyncOnAsync(false);
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        var ct = cancellation.Token;
        await using var listener = await LoopbackSyncListener.StartAsync(pair.Target.Services, ct);
        var penalties = pair.Target.PeerPenaltyAttempts.Count;
        await using (var peer = await LoopbackSyncPeer.ConnectAsync(listener.Endpoint, pair.Target.Identity, pair.Source.Identity, ct))
        {
            await SyncTcpFrameIo.WriteAsync(peer.Stream, SyncTcpMessageType.HelloRequest, CreateHelloRequest(pair.Source.Identity), ct);
            var hello = HelloReply.Parser.ParseFrom((await LoopbackSyncPeer.ReadFrameAsync(peer.Stream, SyncTcpMessageType.HelloReply, ct)).Payload);
            Assert.IsTrue(hello.Ok); Assert.IsTrue(hello.SyncAvailable);
            using var scope = pair.Source.Services.CreateScope();
            var inventory = await scope.ServiceProvider.GetRequiredService<IUserControlOperationAntiEntropyService>()
                .BuildInventoryAsync(pair.Target.Identity.LocalDeviceId, ct);
            await SyncTcpFrameIo.WriteAsync(peer.Stream, SyncTcpMessageType.UserControlOperationInventoryRequest, inventory, ct);
            var reply = UserControlOperationInventoryExchangeReply.Parser.ParseFrom(
                (await LoopbackSyncPeer.ReadFrameAsync(peer.Stream, SyncTcpMessageType.UserControlOperationInventoryReply, ct)).Payload);
            Assert.IsTrue(reply.Users.Count > 0);
        }
        await using (var peer = await LoopbackSyncPeer.ConnectAsync(listener.Endpoint, pair.Target.Identity, pair.Source.Identity, ct))
        {
            await SyncTcpFrameIo.WriteAsync(peer.Stream, SyncTcpMessageType.HelloRequest, CreateHelloRequest(pair.Source.Identity), ct);
            await LoopbackSyncPeer.ReadFrameAsync(peer.Stream, SyncTcpMessageType.HelloReply, ct);
            await SyncTcpFrameIo.WriteAsync(peer.Stream, SyncTcpMessageType.UserSnapshotInventoryRequest,
                new UserSnapshotInventoryExchangeRequest(), ct);
            var error = SyncError.Parser.ParseFrom((await LoopbackSyncPeer.ReadFrameAsync(peer.Stream, SyncTcpMessageType.Error, ct)).Payload);
            Assert.AreEqual(SyncProtocolStatusCode.Unavailable.ToString(), error.Code);
        }
        Assert.AreEqual(penalties, pair.Target.PeerPenaltyAttempts.Count);
        Assert.IsFalse(pair.Target.Identity.IsSyncOn);
    }

    [TestMethod]
    [Timeout(ThreeDeviceTestTimeoutMilliseconds)]
    [TestCategory("Backend")]
    [TestCategory("EndToEnd")]
    [TestCategory("Security")]
    public async Task RevocationReceiptDeletion_ConvergesThroughRelayAfterAccountCleanup_AndSurvivesRestart()
    {
        await using var setup = await CreateThreeDeviceSetupAsync();
        await SynchronizeOneWayUntilAsync(setup.Middle, setup.Original,
            "original learns the final historically authorized member",
            () => HasActiveMembershipForDeviceAsync(setup.Original, setup.NewDevice.Identity.LocalDeviceId));
        var userId = (await setup.Original.GetOnlyLoginIdentityAsync()).UserId;
        foreach (var host in setup.Hosts) host.Transport.Disconnect();
        setup.Original.ConnectTo(setup.Middle); setup.Middle.ConnectTo(setup.Original);
        setup.Middle.ConnectTo(setup.NewDevice); setup.NewDevice.ConnectTo(setup.Middle);
        await setup.Original.Endpoints.DeleteUserAccountAsync(setup.OriginalToken, Encoding.UTF8.GetBytes("P@ssw0rd12345678"));
        Guid operationId;
        using (var scope = setup.Original.Services.CreateScope())
        {
            operationId = (await scope.ServiceProvider.GetRequiredService<IUserControlOperationRepository>()
                .ListForUserAsync(userId)).Single(o => o.OperationType == UserControlOperationType.AccountDeletion).OperationId;
        }
        await SynchronizeOneWayUntilAsync(setup.Original, setup.Middle, "deletion and recipient receipt commit without a session",
            () => HasRevocationReceiptFromAsync(setup.Original, operationId, setup.Middle));
        Assert.IsFalse(await HasRevocationReceiptFromAsync(setup.Original, operationId, setup.NewDevice),
            "The disconnected installation must not receive a fabricated receipt.");
        await SynchronizeOneWayUntilAsync(setup.Middle, setup.NewDevice, "third installation returns its signed receipt to the relay",
            () => HasRevocationReceiptFromAsync(setup.Middle, operationId, setup.NewDevice));
        await SynchronizeOneWayUntilAsync(setup.Middle, setup.Original, "receipt reaches the author without an author-recipient connection",
            () => HasRevocationReceiptFromAsync(setup.Original, operationId, setup.NewDevice));
        foreach (var host in setup.Hosts)
        {
            using var scope = host.Services.CreateScope();
            Assert.IsNull(await scope.ServiceProvider.GetRequiredService<IUserRepository>().GetByIdAsync(userId));
            Assert.IsNotNull(await scope.ServiceProvider.GetRequiredService<IDeletedUserBarrierRepository>().GetAsync(userId));
            Assert.IsNotNull(await scope.ServiceProvider.GetRequiredService<IUserControlOperationRepository>().GetByIdAsync(operationId));
        }
        await using var restarted = await setup.Middle.RestartAsync();
        Assert.IsTrue(await HasRevocationReceiptFromAsync(restarted, operationId, setup.NewDevice));
        using var verify = restarted.Services.CreateScope();
        Assert.IsNotNull(await verify.ServiceProvider.GetRequiredService<IDeletedUserBarrierRepository>().GetAsync(userId));
    }

    private static async Task<bool> HasRevocationReceiptFromAsync(ProductionSyncTestHost holder, Guid operationId,
        ProductionSyncTestHost recipient)
    {
        using var scope = holder.Services.CreateScope();
        var rows = await scope.ServiceProvider.GetRequiredService<IRevocationReceiptRepository>().ListAsync(operationId);
        return rows.Any(row => row.RecipientDeviceId == recipient.Identity.LocalDeviceId &&
            row.RecipientOriginInstanceId == recipient.Identity.OriginInstanceId && row.Kind == RevocationReceiptKind.Applied &&
            RevocationDeliveryReceiptUtil.Deserialize(row.Payload).OperationId == operationId);
    }
}
