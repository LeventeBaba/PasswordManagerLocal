using Google.Protobuf;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using PasswordManagerLocal.Common.Backend.Abstractions.Repositories;
using PasswordManagerLocal.Common.Backend.Abstractions.Services;
using PasswordManagerLocal.Common.Backend.Constants;
using PasswordManagerLocal.Common.Backend.Models;
using PasswordManagerLocal.Common.Backend.Services;
using PasswordManagerLocal.Common.Backend.Sync.Tcp;
using PasswordManagerLocal.Common.Backend.Sync;
using PasswordManagerLocal.Common.Backend.Utils;
using PasswordManagerLocal.Common.Tests.TestInfrastructure;
using System.Buffers.Binary;
using System.Net.Sockets;
using System.Net;
using System.Security.Authentication;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text;

namespace PasswordManagerLocal.Common.Tests.Backend.Services;

public sealed partial class RegularSyncEndToEndTests
{
    [TestMethod]
    [Timeout(TestTimeoutMilliseconds)]
    [TestCategory("SecurityRegression")]
    public async Task EnrolledPeer_OrdinaryLinkDeletionCannotRemoveAuthoritativeLocalProfile()
    {
        await using var pair = await CreateEnrolledPairAsync();
        await RunStepAsync("pause delivery before testing unsigned membership changes", ct => PauseAutomaticDeliveryAsync(pair, ct));
        var userId = (await pair.Target.GetOnlyLoginIdentityAsync()).UserId;
        var privateToken = await RunStepAsync("register the private profile",
            ct => pair.Target.Endpoints.RegisterAsync(pair.Target.CreateRegistrationRequest("private_link_guard"), ct));
        await RunStepAsync("enable the private profile route", ct => pair.Target.EnableSyncAsync(privateToken, ct));
        var privateUser = await GetSecondUserAsync(pair.Target, userId);
        var secret = Encoding.UTF8.GetBytes("local-vault-content");
        await RunStepAsync("write shared profile data",
            ct => AddPasswordAsync(pair.Target, pair.TargetToken, "Shared entry", secret, ct));
        await RunStepAsync("write private profile data",
            ct => AddPasswordAsync(pair.Target, privateToken, "Private entry", secret, ct));

        var unshared = CreateDeviceLinkDelta(pair, privateUser, isDeletion: true, out var privateLinkId);
        var denied = await RunStepAsync("reject deletion for the private profile",
            ct => Assert.ThrowsExactlyAsync<SyncProtocolException>(() => DeliverDeltaAsync(pair, unshared, ct)));
        Assert.AreEqual(SyncProtocolStatusCode.PermissionDenied, denied.StatusCode);

        var delta = CreateDeviceLinkDelta(pair, userId, isDeletion: true, out var linkId);
        using (var scope = pair.Target.Services.CreateScope())
        {
            var services = scope.ServiceProvider;
            Assert.IsTrue((await services.GetRequiredService<IUserMembershipAuthorizationRepository>()
                .ListActiveForDeviceAsync(userId, pair.Target.Identity.LocalDeviceId)).Count > 0);
            Assert.IsTrue(await services.GetRequiredService<ISyncQueueRepository>()
                .HasPendingForModelAsync(userId, SyncModelType.User), "The fixture must have outstanding work for this profile.");
        }

        var sharedDenial = await RunStepAsync("reject an ordinary deletion of authoritative membership",
            ct => Assert.ThrowsExactlyAsync<SyncProtocolException>(() => DeliverDeltaAsync(pair, delta, ct)));
        Assert.AreEqual(SyncProtocolStatusCode.PermissionDenied, sharedDenial.StatusCode);

        using (var scope = pair.Target.Services.CreateScope())
        {
            var services = scope.ServiceProvider;
            Assert.IsNotNull(await services.GetRequiredService<IUserRepository>().GetByIdAsync(userId),
                "A peer's ordinary signed delta must not erase an authoritative local profile.");
            Assert.IsNull(await services.GetRequiredService<ISyncTombstoneRepository>()
                .GetAsync(linkId, SyncModelType.UserDevice));
            Assert.IsNull(await services.GetRequiredService<ISyncTombstoneRepository>()
                .GetAsync(privateLinkId, SyncModelType.UserDevice));
            Assert.IsTrue((await services.GetRequiredService<IUserMembershipAuthorizationRepository>()
                .ListActiveForDeviceAsync(userId, pair.Target.Identity.LocalDeviceId)).Count > 0);
            Assert.IsTrue(await services.GetRequiredService<ISyncQueueRepository>()
                .HasPendingForModelAsync(userId, SyncModelType.User));
        }

        await AssertPasswordSecretAsync(pair.Target, pair.TargetToken, "Shared entry", secret);
        await AssertPasswordSecretAsync(pair.Target, privateToken, "Private entry", secret);
    }

    private static NetworkDelta CreateDeviceLinkDelta(
        SyncPair pair, Guid userId, bool isDeletion, out Guid modelId, int minimumPayloadBytes = 0)
    {
        var timestamp = DateTimeOffset.UtcNow.AddSeconds(2).ToUnixTimeMilliseconds();
        var sender = pair.Source.Identity;
        var receiver = pair.Target.Identity;
        var link = new UserDeviceSyncPayload
        {
            UserId = userId,
            DeviceId = isDeletion ? receiver.LocalDeviceId : sender.LocalDeviceId,
            IsSyncOn = !isDeletion,
            IsDeleted = isDeletion,
            DeletedAt = isDeletion ? DateTimeOffset.FromUnixTimeMilliseconds(timestamp) : null
        };
        link.IntegrityHash = SyncHashUtil.CalculateUserDeviceHash(link, timestamp);
        modelId = SyncIdentityUtil.BuildUserDeviceModelId(link.UserId, link.DeviceId);
        var payload = new SyncDeltaPayload
        {
            ModelId = modelId,
            ModelType = SyncModelType.UserDevice,
            ChangeType = isDeletion ? SyncChangeType.Deleted : SyncChangeType.Updated,
            UserDevice = link
        };
        var plaintext = JsonSerializer.SerializeToUtf8Bytes(payload, BackendJsonSerializerContext.Default.SyncDeltaPayload);
        if (plaintext.Length < minimumPayloadBytes)
        {
            // JSON permits trailing whitespace. Keep the signed payload valid at the wire size limit.
            var jsonLength = plaintext.Length;
            Array.Resize(ref plaintext, minimumPayloadBytes);
            plaintext.AsSpan(jsonLength).Fill((byte)' ');
        }
        try
        {
            var delta = new NetworkDelta
            {
                Entity = $"UserDevice:{payload.ChangeType}:{modelId:N}",
                DeviceId = sender.DeviceIdHex,
                SignPub = sender.SignPublicKey,
                RecipientDeviceId = receiver.LocalDeviceId.ToString("N"),
                Ts = timestamp,
                EncryptionVersion = SyncConstants.SyncDeltaEncryptionVersion,
                PayloadHash = SHA256.HashData(plaintext)
            };
            delta.Payload = sender.EncryptForDevice(plaintext, receiver.AgreementPublicKey,
                SyncCryptoUtil.BuildAssociatedData(delta), out var ephemeral, out var nonce, out var tag);
            delta.EphemeralPublicKey = ephemeral;
            delta.Nonce = nonce;
            delta.Tag = tag;
            NetDeltaSigner.FillSignature(delta, sender);
            return delta;
        }
        finally
        {
            CryptographicOperations.ZeroMemory(plaintext);
        }
    }

    [TestMethod]
    [DataRow(SyncTcpMessageType.PushDeltaStart, "device")]
    [DataRow(SyncTcpMessageType.PushDeltaStart, "key")]
    [DataRow(SyncTcpMessageType.PushDeltaStart, "both")]
    [DataRow(SyncTcpMessageType.UserSnapshotInventoryRequest, "both")]
    [DataRow(SyncTcpMessageType.UserSnapshotRequestBatch, "both")]
    [DataRow(SyncTcpMessageType.UserControlOperationInventoryRequest, "both")]
    [DataRow(SyncTcpMessageType.UserControlOperationRequestBatch, "both")]
    [Timeout(TestTimeoutMilliseconds)]
    [TestCategory("SecurityRegression")]
    public async Task OutgoingSync_StopsBeforeSendingProtectedFramesWhenPinnedPeerClaimsDifferentIdentity(
        SyncTcpMessageType operation, string changed)
    {
        await using var pair = await CreateEnrolledPairAsync();
        await PauseAutomaticDeliveryAsync(pair);
        await pair.Source.CacheEndpointForAsync(pair.Target);
        pair.Source.EndpointsCache.Clear();
        var identities = pair.Source.Services.GetRequiredService<ISyncDeviceIdentityService>();
        Assert.IsTrue(identities.ContainsFingerprint(pair.Target.Identity.FingerprintHex));
        pair.Source.Presence.InvalidateAll("Isolate the outgoing TLS exchange.");
        var client = CreateTcpClient(pair.Source);
        var delta = await BuildLatestUserDeltaAsync(pair.Source, pair.Target);
        var reply = CreateHelloReply(pair.Target.Identity);
        if (changed is "device" or "both")
            reply.DeviceId = pair.Source.Identity.DeviceIdHex;
        if (changed is "key" or "both")
            reply.SignPub = ByteString.CopyFrom(pair.Source.Identity.SignPublicKey);

        using var loopback = new TcpListener(IPAddress.Loopback, 0);
        loopback.Start();
        var port = ((IPEndPoint)loopback.LocalEndpoint).Port;
        using var deadline = new CancellationTokenSource(StepTimeout);
        var received = CaptureAfterHelloAsync(loopback, pair, reply, deadline.Token);
        var exchange = TryOutgoingOperationAsync(client, operation, port, pair.Target.Identity.FingerprintHex, delta, deadline.Token);
        await Task.WhenAll(received, exchange);
        Assert.IsNull(await received, $"The client sent {received.Result?.Type} after the mismatched Hello.");
        Assert.IsFalse(await exchange);
        Assert.IsFalse(pair.Source.IsOnline(pair.Target));
    }

    [TestMethod]
    [DataRow(SyncTcpMessageType.PushDeltaStart)]
    [DataRow(SyncTcpMessageType.UserSnapshotInventoryRequest)]
    [DataRow(SyncTcpMessageType.UserSnapshotRequestBatch)]
    [DataRow(SyncTcpMessageType.UserControlOperationInventoryRequest)]
    [DataRow(SyncTcpMessageType.UserControlOperationRequestBatch)]
    [Timeout(TestTimeoutMilliseconds)]
    [TestCategory("Integration")]
    public async Task ProductionTcp_RequiresHelloOnEveryConnection(SyncTcpMessageType operation)
    {
        await using var pair = await CreateEnrolledPairAsync();
        await PauseAutomaticDeliveryAsync(pair);
        using var deadline = new CancellationTokenSource(StepTimeout);
        var ct = deadline.Token;
        await using var listener = await LoopbackSyncListener.StartAsync(pair.Target.Services, ct);
        await using (var first = await LoopbackSyncPeer.ConnectAsync(listener.Endpoint, pair.Target.Identity, pair.Source.Identity, ct))
        {
            await AuthenticateSyncAsync(first.Stream, pair.Source.Identity, ct);
            await SyncTcpFrameIo.WriteAsync(first.Stream, SyncTcpMessageType.PushDeltaStart, ct);
            await SyncTcpFrameIo.WriteAsync(first.Stream, SyncTcpMessageType.PushDeltaEnd, ct);
            await LoopbackSyncPeer.ReadFrameAsync(first.Stream, SyncTcpMessageType.Ack, ct);
            Assert.IsNull(await LoopbackSyncPeer.ReadUntilCloseAsync(first.Stream, ct));
        }

        pair.Target.Presence.InvalidateAll("The second socket must authenticate independently.");
        await using var second = await LoopbackSyncPeer.ConnectAsync(listener.Endpoint, pair.Target.Identity, pair.Source.Identity, ct);
        await SyncTcpFrameIo.WriteAsync(second.Stream, operation, ct);
        var error = (await LoopbackSyncPeer.ReadFrameAsync(second.Stream, SyncTcpMessageType.Error, ct)).Parse(SyncError.Parser);
        Assert.AreEqual(SyncProtocolStatusCode.FailedPrecondition.ToString(), error.Code);
        Assert.IsNull(await LoopbackSyncPeer.ReadUntilCloseAsync(second.Stream, ct));
        Assert.IsFalse(pair.Target.IsOnline(pair.Source));
    }

    [TestMethod]
    [DataRow("missing")]
    [DataRow("unknown")]
    [DataRow("wrong-claim")]
    [Timeout(TestTimeoutMilliseconds)]
    [TestCategory("Integration")]
    public async Task ProductionTcp_RejectsUnboundCertificateAndHello(string scenario)
    {
        await using var pair = await CreateEnrolledPairAsync();
        await PauseAutomaticDeliveryAsync(pair);
        await using var unknown = await ProductionEnrollmentTestHost.CreateAsync();
        using var deadline = new CancellationTokenSource(StepTimeout);
        var ct = deadline.Token;
        await using var listener = await LoopbackSyncListener.StartAsync(pair.Target.Services, ct);
        pair.Target.Presence.InvalidateAll("Isolate the incoming TLS exchange.");
        var certificate = scenario switch
        {
            "missing" => null,
            "unknown" => unknown.Identity,
            _ => pair.Source.Identity
        };
        var request = CreateHelloRequest(scenario == "wrong-claim" ? unknown.Identity : pair.Source.Identity);
        try
        {
            await using var peer = await LoopbackSyncPeer.ConnectAsync(listener.Endpoint, pair.Target.Identity, certificate, ct);
            using var frames = new MemoryStream();
            await SyncTcpFrameIo.WriteAsync(frames, SyncTcpMessageType.HelloRequest, request, ct);
            await SyncTcpFrameIo.WriteAsync(frames, SyncTcpMessageType.UserSnapshotInventoryRequest, ct);
            await peer.Stream.WriteAsync(frames.ToArray(), ct);

            var response = await LoopbackSyncPeer.ReadUntilCloseAsync(peer.Stream, ct);
            if (scenario != "missing")
                Assert.IsNotNull(response, "An independently issued certificate should reach the application Hello gate.");
            if (response is not null)
            {
                Assert.AreEqual(SyncTcpMessageType.HelloReply, response.Type);
                Assert.IsFalse(response.Parse(HelloReply.Parser).Ok);
                Assert.IsNull(await LoopbackSyncPeer.ReadUntilCloseAsync(peer.Stream, ct));
            }
        }
        catch (Exception ex) when (scenario == "missing" && ex is AuthenticationException or IOException)
        {
            // The missing certificate can be reported at handshake or first application I/O.
        }
        Assert.IsFalse(pair.Target.IsOnline(pair.Source));
        Assert.IsFalse(pair.Target.Presence.IsOnline(unknown.Identity.FingerprintHex, StepTimeout));
    }

    private static TcpSyncClientService CreateTcpClient(ProductionSyncTestHost host) =>
        new(host.Identity, host.Services.GetRequiredService<ISyncDeviceIdentityService>(), host.Presence,
            host.Services.GetRequiredService<IServiceScopeFactory>());

    private static HelloReply CreateHelloReply(IDeviceIdentityService identity) => new()
    {
        Ok = true,
        SyncAvailable = true,
        ProtocolVersion = SyncConstants.SyncProtocolVersion,
        DeviceId = identity.DeviceIdHex,
        SignPub = ByteString.CopyFrom(identity.SignPublicKey)
    };

    private static async Task<HelloReply> AuthenticateSyncAsync(Stream stream, IDeviceIdentityService identity, CancellationToken ct)
    {
        await SyncTcpFrameIo.WriteAsync(stream, SyncTcpMessageType.HelloRequest, CreateHelloRequest(identity), ct);
        var frame = await LoopbackSyncPeer.ReadFrameAsync(stream, SyncTcpMessageType.HelloReply, ct);
        var reply = frame.Parse(HelloReply.Parser);
        Assert.IsTrue(reply.Ok, "The enrolled test device must authenticate.");
        return reply;
    }

    private static async Task<SyncTcpFrame?> CaptureAfterHelloAsync(
        TcpListener listener, SyncPair pair, HelloReply reply, CancellationToken ct)
    {
        await using var peer = await LoopbackSyncPeer.AcceptAsync(listener, pair.Target.Identity, pair.Source.Identity, ct);
        await LoopbackSyncPeer.ReadFrameAsync(peer.Stream, SyncTcpMessageType.HelloRequest, ct);
        await SyncTcpFrameIo.WriteAsync(peer.Stream, SyncTcpMessageType.HelloReply, reply, ct);
        return await LoopbackSyncPeer.ReadUntilCloseAsync(peer.Stream, ct);
    }

    private static async Task<bool> TryOutgoingOperationAsync(TcpSyncClientService client, SyncTcpMessageType operation,
        int port, string fingerprint, NetworkDelta delta, CancellationToken ct)
    {
        const string host = "127.0.0.1";
        try
        {
            switch (operation)
            {
                case SyncTcpMessageType.PushDeltaStart:
                    return await client.SendDeltasAsync(host, port, fingerprint, [delta], ct);
                case SyncTcpMessageType.UserSnapshotInventoryRequest:
                    await client.ExchangeUserSnapshotInventoryAsync(host, port, fingerprint, new(), ct);
                    return true;
                case SyncTcpMessageType.UserSnapshotRequestBatch:
                    await client.RequestUserSnapshotsAsync(host, port, fingerprint, new(), ct);
                    return true;
                case SyncTcpMessageType.UserControlOperationInventoryRequest:
                    await client.ExchangeUserControlOperationInventoryAsync(host, port, fingerprint, new(), ct);
                    return true;
                case SyncTcpMessageType.UserControlOperationRequestBatch:
                    await client.RequestUserControlOperationsAsync(host, port, fingerprint, new(), ct);
                    return true;
                default:
                    throw new ArgumentOutOfRangeException(nameof(operation));
            }
        }
        catch (Exception ex) when (ex is IOException || ex is InvalidDataException)
        {
            return false;
        }
    }

    [TestMethod]
    [DataRow("invalid-signature")]
    [DataRow("wrong-recipient")]
    [DataRow("invalid-first")]
    [DataRow("malformed-protobuf")]
    [DataRow("missing-end")]
    [Timeout(TestTimeoutMilliseconds)]
    [TestCategory("Integration")]
    public async Task TcpDeltaPush_InvalidOrTruncatedBatchDoesNotAcknowledgeOrPersistInvalidItems(string fault)
    {
        await using var pair = await CreateEnrolledPairAsync();
        await PauseAutomaticDeliveryAsync(pair);
        var secret = Encoding.UTF8.GetBytes("synthetic-wire-delta");
        await AddPasswordAsync(pair.Source, pair.SourceToken, "Valid prefix", secret);
        var prefix = await BuildLatestUserDeltaAsync(pair.Source, pair.Target);
        await AddPasswordAsync(pair.Source, pair.SourceToken, "Invalid suffix", secret);
        var suffix = await BuildLatestUserDeltaAsync(pair.Source, pair.Target);
        Assert.IsTrue(prefix.SnapshotOriginRevision < suffix.SnapshotOriginRevision);
        var bad = DeltaMapping.FromProto(DeltaMapping.ToProto(suffix));
        if (fault == "wrong-recipient")
        {
            bad.RecipientDeviceId = pair.Source.Identity.LocalDeviceId.ToString("N");
            NetDeltaSigner.FillSignature(bad, pair.Source.Identity);
        }
        else
        {
            bad.Sig[0] ^= 1;
        }

        using var deadline = new CancellationTokenSource(StepTimeout);
        var ct = deadline.Token;
        await using var listener = await LoopbackSyncListener.StartAsync(pair.Target.Services, ct);
        await using (var peer = await LoopbackSyncPeer.ConnectAsync(listener.Endpoint, pair.Target.Identity, pair.Source.Identity, ct))
        {
            await AuthenticateSyncAsync(peer.Stream, pair.Source.Identity, ct);
            await SyncTcpFrameIo.WriteAsync(peer.Stream, SyncTcpMessageType.PushDeltaStart, ct);
            if (fault != "invalid-first")
                await SyncTcpFrameIo.WriteAsync(peer.Stream, SyncTcpMessageType.DeltaChunk, DeltaMapping.ToProto(prefix), ct);
            switch (fault)
            {
                case "malformed-protobuf":
                    // One type byte and a protobuf field with an unterminated varint.
                    await peer.Stream.WriteAsync(new byte[] { 0, 0, 0, 2, (byte)SyncTcpMessageType.DeltaChunk, 0x80 }, ct);
                    break;
                case "missing-end":
                    await peer.Stream.ShutdownAsync().WaitAsync(ct);
                    break;
                default:
                    await SyncTcpFrameIo.WriteAsync(peer.Stream, SyncTcpMessageType.DeltaChunk, DeltaMapping.ToProto(bad), ct);
                    break;
            }

            var response = await LoopbackSyncPeer.ReadUntilCloseAsync(peer.Stream, ct);
            if (response is not null)
            {
                Assert.AreEqual(SyncTcpMessageType.Error, response.Type, "A failed push must never return an acknowledgement.");
                Assert.IsNull(await LoopbackSyncPeer.ReadUntilCloseAsync(peer.Stream, ct));
            }
        }

        Assert.AreEqual(fault != "invalid-first", await HasPasswordAsync(pair.Target, pair.TargetToken, "Valid prefix"));
        Assert.IsFalse(await HasPasswordAsync(pair.Target, pair.TargetToken, "Invalid suffix"));
        var checkpoint = await pair.Target.GetOnlyCanonicalCheckpointAsync();
        Assert.IsNotNull(checkpoint);
        using (var scope = pair.Target.Services.CreateScope())
        {
            var retained = await scope.ServiceProvider.GetRequiredService<IUserSyncSnapshotRepository>().GetExactAsync(
                suffix.SnapshotUserId, suffix.SnapshotOriginDeviceId, suffix.SnapshotOriginInstanceId,
                checkpoint.KeyEpoch, suffix.SnapshotOriginRevision, ct);
            Assert.IsNull(retained);
        }

        var client = CreateTcpClient(pair.Source);
        var host = listener.Endpoint.Address.ToString();
        var pin = pair.Target.Identity.FingerprintHex;
        Assert.IsTrue(await client.SendDeltasAsync(host, listener.Endpoint.Port, pin, [prefix, suffix], ct));
        Assert.IsTrue(await client.SendDeltasAsync(host, listener.Endpoint.Port, pin, [prefix, suffix], ct));
        await AssertPasswordSecretAsync(pair.Target, pair.TargetToken, "Valid prefix", secret);
        await AssertPasswordSecretAsync(pair.Target, pair.TargetToken, "Invalid suffix", secret);
    }

    [TestMethod]
    [DataRow(false, "missing")]
    [DataRow(false, "profile")]
    [DataRow(false, "installation")]
    [DataRow(false, "revision")]
    [DataRow(false, "hash")]
    [DataRow(false, "quarantined")]
    [DataRow(false, "conflicting")]
    [DataRow(false, "conflicting-hash")]
    [DataRow(false, "durable-duplicate")]
    [DataRow(false, "exact")]
    [DataRow(true, "missing")]
    [DataRow(true, "operation")]
    [DataRow(true, "installation")]
    [DataRow(true, "revision")]
    [DataRow(true, "hash")]
    [DataRow(true, "quarantined")]
    [DataRow(true, "conflicting")]
    [DataRow(true, "conflicting-hash")]
    [DataRow(true, "durable-duplicate")]
    [DataRow(true, "exact")]
    [Timeout(TestTimeoutMilliseconds)]
    [TestCategory("SecurityRegression")]
    public async Task OutgoingPush_RequiresUnambiguousDurableReceiptForExactItem(bool control, string fault)
    {
        await using var pair = await CreateEnrolledPairAsync();
        await PauseAutomaticDeliveryAsync(pair);
        var delta = control ? await BuildAdditionDeltaAsync(pair) : await BuildLatestUserDeltaAsync(pair.Source, pair.Target);
        pair.Source.Services.GetRequiredService<ISyncDeviceIdentityService>().Clear();
        var ack = CreateReceipt(delta, control, fault);
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        using var deadline = new CancellationTokenSource(StepTimeout);
        var ct = deadline.Token;
        var serving = ServeAckAsync(listener, pair, ack, ct);
        var result = CreateTcpClient(pair.Source).SendDeltasAsync("127.0.0.1", ((IPEndPoint)listener.LocalEndpoint).Port,
            pair.Target.Identity.FingerprintHex, [delta], ct);
        await Task.WhenAll(serving, result);
        Assert.AreEqual(fault is "exact" or "durable-duplicate", await result);
    }

    [TestMethod]
    [Timeout(TestTimeoutMilliseconds)]
    [TestCategory("Integration")]
    public async Task QueueWorker_KeepsUnacknowledgedTcpWorkAndDrainsItAfterGenuineRetry()
    {
        await using var pair = await CreateEnrolledPairAsync();
        await PauseAutomaticDeliveryAsync(pair);
        const string entry = "Retry after incomplete receipt";
        var secret = Encoding.UTF8.GetBytes("synthetic-queue-secret");
        await AddPasswordAsync(pair.Source, pair.SourceToken, entry, secret);
        Assert.IsTrue(await pair.Source.HasPendingForAsync(pair.Target.Identity.LocalDeviceId));

        using var deadline = new CancellationTokenSource(StepTimeout);
        var ct = deadline.Token;
        using var scripted = new TcpListener(IPAddress.Loopback, 0);
        scripted.Start();
        var scriptedPort = ((IPEndPoint)scripted.LocalEndpoint).Port;
        var client = CreateTcpClient(pair.Source);
        var fingerprint = pair.Target.Identity.FingerprintHex;
        var serving = ServeTimestampOnlyAckAsync(scripted, pair, ct);
        pair.Source.Transport.DeltaSendOverride = (deltas, cancellation) =>
            client.SendDeltasAsync("127.0.0.1", scriptedPort, fingerprint, deltas, cancellation);
        try
        {
            await Task.WhenAll(serving, StartSyncAsync(pair.Source, pair.Target, "send to a peer that omits receipts", ct));
            Assert.IsTrue(await pair.Source.HasPendingForAsync(pair.Target.Identity.LocalDeviceId));
            Assert.IsFalse(await HasPasswordAsync(pair.Target, pair.TargetToken, entry));

            await using var receiver = await LoopbackSyncListener.StartAsync(pair.Target.Services, ct);
            Task<bool> SendToReceiverAsync(IReadOnlyList<NetworkDelta> deltas, CancellationToken cancellation) =>
                client.SendDeltasAsync(receiver.Endpoint.Address.ToString(), receiver.Endpoint.Port, fingerprint, deltas, cancellation);

            var receiptArrived = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            var releaseReceipt = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var calls = 0;
            pair.Source.Transport.DeltaSendOverride = async (deltas, cancellation) =>
            {
                if (Interlocked.Increment(ref calls) != 1)
                    return false;
                var accepted = await SendToReceiverAsync(deltas, cancellation);
                receiptArrived.TrySetResult(accepted);
                await releaseReceipt.Task.WaitAsync(cancellation);
                return accepted;
            };

            const string nextEntry = "Queued while prior acknowledgement was in flight";
            var sending = StartSyncAsync(pair.Source, pair.Target, "hold a genuine receipt before queue acknowledgement", ct);
            try
            {
                Assert.IsTrue(await receiptArrived.Task.WaitAsync(ct));
                await AssertPasswordSecretAsync(pair.Target, pair.TargetToken, entry, secret);
                await AddPasswordAsync(pair.Source, pair.SourceToken, nextEntry, secret);
            }
            finally
            {
                releaseReceipt.TrySetResult();
                await sending;
            }

            Assert.IsTrue(await pair.Source.HasPendingForAsync(pair.Target.Identity.LocalDeviceId),
                "An old acknowledgement must leave the replacement generation pending.");
            Assert.IsFalse(await HasPasswordAsync(pair.Target, pair.TargetToken, nextEntry));
            pair.Source.Transport.DeltaSendOverride = SendToReceiverAsync;
            await SynchronizeOneWayUntilAsync(pair.Source, pair.Target, "retry the replacement generation over TCP",
                async () => !await pair.Source.HasPendingForAsync(pair.Target.Identity.LocalDeviceId) &&
                    await HasPasswordAsync(pair.Target, pair.TargetToken, nextEntry));
            await AssertPasswordSecretAsync(pair.Target, pair.TargetToken, nextEntry, secret);
        }
        finally
        {
            pair.Source.Transport.DeltaSendOverride = null;
        }
    }

    [TestMethod]
    [Timeout(TestTimeoutMilliseconds)]
    [TestCategory("SecurityRegression")]
    public async Task TcpControlRelay_PrivateAndUnknownOperationsHaveIndistinguishableDenials()
    {
        await using var pair = await CreateEnrolledPairAsync();
        await PauseAutomaticDeliveryAsync(pair);
        var sharedUser = (await pair.Target.GetOnlyLoginIdentityAsync()).UserId;
        var privateToken = await pair.Target.Endpoints.RegisterAsync(pair.Target.CreateRegistrationRequest("private_control_guard"));
        Guid privateUser;
        using (var scope = pair.Target.Services.CreateScope())
        {
            privateUser = (await scope.ServiceProvider.GetRequiredService<IUserRepository>().ListUserIdsAsync())
                .Single(id => id != sharedUser);
        }
        await pair.Target.Endpoints.DeleteUserAccountAsync(privateToken, Encoding.UTF8.GetBytes("P@ssw0rd12345678"));
        UserControlOperation deletion;
        using (var scope = pair.Target.Services.CreateScope())
        {
            deletion = (await scope.ServiceProvider.GetRequiredService<IUserControlOperationRepository>().ListForUserAsync(privateUser))
                .Single(row => row.OperationType == UserControlOperationType.AccountDeletion);
        }

        using var deadline = new CancellationTokenSource(StepTimeout);
        var ct = deadline.Token;
        await using var listener = await LoopbackSyncListener.StartAsync(pair.Target.Services, ct);
        var request = CreateControlRequest(deletion);
        var privateError = await RequestDeniedControlAsync(pair, listener, request, ct);
        var missing = request.Clone();
        missing.OperationId = Guid.NewGuid().ToString("N");
        var absentError = await RequestDeniedControlAsync(pair, listener, missing, ct);
        var mislabeled = request.Clone();
        mislabeled.UserId = sharedUser.ToString("N");
        var wrongProfileError = await RequestDeniedControlAsync(pair, listener, mislabeled, ct);
        Assert.AreEqual(absentError, privateError, "A peer should not distinguish a private operation from an absent one.");
        Assert.AreEqual(absentError, wrongProfileError);
    }

    private static async Task<NetworkDelta> BuildAdditionDeltaAsync(SyncPair pair)
    {
        using var scope = pair.Source.Services.CreateScope();
        var services = scope.ServiceProvider;
        var userId = (await pair.Source.GetOnlyLoginIdentityAsync()).UserId;
        var addition = (await services.GetRequiredService<IUserControlOperationRepository>().ListForUserAsync(userId))
            .Single(row => row.OperationType == UserControlOperationType.DeviceAddition);
        var device = await services.GetRequiredService<IDeviceRepository>().GetByIdWithUserDevicesAsync(pair.Target.Identity.LocalDeviceId);
        Assert.IsNotNull(device);
        return await services.GetRequiredService<IOutgoingDeltaBuilderService>().BuildUserControlOperationRelayAsync(addition, device);
    }

    private static Ack CreateReceipt(NetworkDelta delta, bool control, string fault)
    {
        var ack = new Ack { LastSyncedTs = delta.Ts + 1 };
        if (fault == "missing")
            return ack;

        if (control)
        {
            var receipt = new UserControlOperationReceipt
            {
                OperationId = (fault == "operation" ? Guid.NewGuid() : delta.ControlOperationId).ToString("N"),
                UserId = delta.ControlOperationUserId.ToString("N"),
                OriginDeviceId = delta.ControlOperationOriginDeviceId.ToString("N"),
                OriginInstanceId = (fault == "installation" ? Guid.NewGuid() : delta.ControlOperationOriginInstanceId).ToString("N"),
                OriginSequence = delta.ControlOperationOriginSequence + (fault == "revision" ? 1 : 0),
                OperationHash = ReceiptHash(delta.ControlOperationHash, fault),
                State = fault == "quarantined" ? UserControlOperationReceiptStateProto.UserControlOperationReceiptQuarantined :
                    UserControlOperationReceiptStateProto.UserControlOperationReceiptApplied
            };
            ack.UserControlOperationReceipts.Add(receipt);
            if (fault is "conflicting" or "conflicting-hash" or "durable-duplicate")
            {
                var duplicate = receipt.Clone();
                if (fault == "conflicting")
                    duplicate.State = UserControlOperationReceiptStateProto.UserControlOperationReceiptQuarantined;
                if (fault == "conflicting-hash")
                    duplicate.OperationHash = ReceiptHash(delta.ControlOperationHash, "hash");
                if (fault == "durable-duplicate")
                    duplicate.State = UserControlOperationReceiptStateProto.UserControlOperationReceiptAlreadyStored;
                ack.UserControlOperationReceipts.Add(duplicate);
            }
        }
        else
        {
            var receipt = new UserSnapshotReceipt
            {
                UserId = (fault == "profile" ? Guid.NewGuid() : delta.SnapshotUserId).ToString("N"),
                OriginDeviceId = delta.SnapshotOriginDeviceId.ToString("N"),
                OriginInstanceId = (fault == "installation" ? Guid.NewGuid() : delta.SnapshotOriginInstanceId).ToString("N"),
                OriginRevision = delta.SnapshotOriginRevision + (fault == "revision" ? 1 : 0),
                SnapshotHash = ReceiptHash(delta.SnapshotHash, fault),
                State = fault == "quarantined" ? UserSnapshotReceiptStateProto.UserSnapshotReceiptQuarantined :
                    UserSnapshotReceiptStateProto.UserSnapshotReceiptMergedImmediately
            };
            ack.UserSnapshotReceipts.Add(receipt);
            if (fault is "conflicting" or "conflicting-hash" or "durable-duplicate")
            {
                var duplicate = receipt.Clone();
                if (fault == "conflicting")
                    duplicate.State = UserSnapshotReceiptStateProto.UserSnapshotReceiptQuarantined;
                if (fault == "conflicting-hash")
                    duplicate.SnapshotHash = ReceiptHash(delta.SnapshotHash, "hash");
                if (fault == "durable-duplicate")
                    duplicate.State = UserSnapshotReceiptStateProto.UserSnapshotReceiptAlreadyStored;
                ack.UserSnapshotReceipts.Add(duplicate);
            }
        }

        return ack;
    }

    private static ByteString ReceiptHash(byte[] hash, string fault)
    {
        if (fault != "hash")
            return ByteString.CopyFrom(hash);
        var changed = hash.ToArray();
        changed[0] ^= 1;
        return ByteString.CopyFrom(changed);
    }

    private static async Task ServeAckAsync(TcpListener listener, SyncPair pair, Ack ack, CancellationToken ct)
    {
        await using var peer = await LoopbackSyncPeer.AcceptAsync(listener, pair.Target.Identity, pair.Source.Identity, ct);
        await LoopbackSyncPeer.ReadFrameAsync(peer.Stream, SyncTcpMessageType.HelloRequest, ct);
        await SyncTcpFrameIo.WriteAsync(peer.Stream, SyncTcpMessageType.HelloReply, CreateHelloReply(pair.Target.Identity), ct);
        await LoopbackSyncPeer.ReadFrameAsync(peer.Stream, SyncTcpMessageType.PushDeltaStart, ct);
        await LoopbackSyncPeer.ReadFrameAsync(peer.Stream, SyncTcpMessageType.DeltaChunk, ct);
        await LoopbackSyncPeer.ReadFrameAsync(peer.Stream, SyncTcpMessageType.PushDeltaEnd, ct);
        await SyncTcpFrameIo.WriteAsync(peer.Stream, SyncTcpMessageType.Ack, ack, ct);
    }

    private static async Task ServeTimestampOnlyAckAsync(TcpListener listener, SyncPair pair, CancellationToken ct)
    {
        await using var peer = await LoopbackSyncPeer.AcceptAsync(listener, pair.Target.Identity, pair.Source.Identity, ct);
        await LoopbackSyncPeer.ReadFrameAsync(peer.Stream, SyncTcpMessageType.HelloRequest, ct);
        await SyncTcpFrameIo.WriteAsync(peer.Stream, SyncTcpMessageType.HelloReply, CreateHelloReply(pair.Target.Identity), ct);
        await LoopbackSyncPeer.ReadFrameAsync(peer.Stream, SyncTcpMessageType.PushDeltaStart, ct);
        var timestamp = 0L;
        while (true)
        {
            var frame = await SyncTcpFrameIo.ReadAsync(peer.Stream, ct);
            Assert.IsNotNull(frame);
            if (frame.Type == SyncTcpMessageType.PushDeltaEnd)
                break;
            Assert.AreEqual(SyncTcpMessageType.DeltaChunk, frame.Type);
            timestamp = Math.Max(timestamp, frame.Parse(DeltaChunk.Parser).Ts);
        }

        Assert.IsTrue(timestamp > 0);
        await SyncTcpFrameIo.WriteAsync(peer.Stream, SyncTcpMessageType.Ack, new Ack { LastSyncedTs = timestamp }, ct);
    }

    private static UserControlOperationRequest CreateControlRequest(UserControlOperation operation) => new()
    {
        OperationId = operation.OperationId.ToString("N"),
        UserId = operation.UserId.ToString("N"),
        OriginDeviceId = operation.OriginDeviceId.ToString("N"),
        OriginInstanceId = operation.OriginInstanceId.ToString("N"),
        OriginSequence = operation.OriginSequence,
        OperationType = (int)operation.OperationType,
        PreviousKeyEpoch = operation.PreviousKeyEpoch,
        ResultingKeyEpoch = operation.ResultingKeyEpoch,
        PreviousMembershipEpoch = operation.PreviousMembershipEpoch,
        ResultingMembershipEpoch = operation.ResultingMembershipEpoch,
        ExpectedOperationHash = ByteString.CopyFrom(operation.OperationHash)
    };

    private static async Task<SyncError> RequestDeniedControlAsync(
        SyncPair pair, LoopbackSyncListener listener, UserControlOperationRequest request, CancellationToken ct)
    {
        await using var peer = await LoopbackSyncPeer.ConnectAsync(listener.Endpoint, pair.Target.Identity, pair.Source.Identity, ct);
        await AuthenticateSyncAsync(peer.Stream, pair.Source.Identity, ct);
        var batch = new UserControlOperationRequestBatch();
        batch.Requests.Add(request);
        await SyncTcpFrameIo.WriteAsync(peer.Stream, SyncTcpMessageType.UserControlOperationRequestBatch, batch, ct);
        var error = (await LoopbackSyncPeer.ReadFrameAsync(peer.Stream, SyncTcpMessageType.Error, ct)).Parse(SyncError.Parser);
        Assert.IsNull(await LoopbackSyncPeer.ReadUntilCloseAsync(peer.Stream, ct));
        return error;
    }

    [TestMethod]
    [Timeout(ThreeDeviceTestTimeoutMilliseconds)]
    [TestCategory("Integration")]
    public async Task TcpRevocation_ExistingAndNewConnectionsCannotReadRevokedProfile()
    {
        await using var pair = await CreateEnrolledPairAsync();
        await PauseAutomaticDeliveryAsync(pair);
        var retainedUser = (await pair.Source.GetOnlyLoginIdentityAsync()).UserId;
        const string revokedName = "revoked_wire_profile";
        var revokedToken = await pair.Source.Endpoints.RegisterAsync(pair.Source.CreateRegistrationRequest(revokedName));
        await pair.Source.EnableSyncAsync(revokedToken);
        var code = await pair.Target.Endpoints.StartDeviceEnrollmentAsync();
        await pair.Source.Endpoints.AddDeviceByCodeAsync(revokedToken, code.Code);
        var remoteToken = await pair.Target.Endpoints.LoginAsync(pair.Target.CreateLoginRequest(revokedName));
        await pair.Target.EnableSyncAsync(remoteToken);
        var revokedUser = await GetSecondUserAsync(pair.Source, retainedUser);
        await RunStepAsync("pause delivery after enrolling the profile to be revoked",
            ct => PauseAutomaticDeliveryAsync(pair, ct));

        // Retain the old entity in the removal scope while a different scope commits a
        // new generation. Revocation must refresh it before preparing the signed cutoff.
        using var removalScope = pair.Source.Services.CreateScope();
        var removalUsers = removalScope.ServiceProvider.GetRequiredService<IUserRepository>();
        var tracked = await removalUsers.GetByIdAsync(revokedUser);
        Assert.IsNotNull(tracked);
        await RunStepAsync("advance canonical data after tracking the revocation candidate",
            ct => AddPasswordAsync(pair.Source, revokedToken, "Latest revocation entry",
                Encoding.UTF8.GetBytes("synthetic-revocation-secret"), ct));
        var current = await removalUsers.GetByIdAsNoTrackingAsync(revokedUser);
        Assert.IsNotNull(current);
        Assert.IsFalse(tracked.EncryptedUserPasswordsDataPayload.AsSpan().SequenceEqual(current.EncryptedUserPasswordsDataPayload));

        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(40));
        var ct = deadline.Token;
        await using var listener = await LoopbackSyncListener.StartAsync(pair.Source.Services, ct);
        await using var held = await LoopbackSyncPeer.ConnectAsync(listener.Endpoint, pair.Source.Identity, pair.Target.Identity, ct);
        await AuthenticateSyncAsync(held.Stream, pair.Target.Identity, ct);
        var removal = await removalScope.ServiceProvider.GetRequiredService<IUserDeviceDisconnectionService>()
            .DisconnectUserDeviceAsync(revokedToken, pair.Target.Identity.LocalDeviceId,
                Encoding.UTF8.GetBytes("P@ssw0rd12345678"), ct);
        Assert.IsTrue(removal.Removed);
        Assert.IsNotNull(removal.OperationId);
        Assert.IsTrue(await HasPasswordAsync(pair.Source, revokedToken, "Latest revocation entry"));

        var revokedSnapshot = await LoadSnapshotRequestAsync(pair.Source, revokedUser, ct);
        await AssertSnapshotRelayDeniedAsync(held.Stream, revokedSnapshot, ct);
        await using (var fresh = await LoopbackSyncPeer.ConnectAsync(listener.Endpoint, pair.Source.Identity, pair.Target.Identity, ct))
        {
            await AuthenticateSyncAsync(fresh.Stream, pair.Target.Identity, ct);
            await AssertSnapshotRelayDeniedAsync(fresh.Stream, revokedSnapshot, ct);
        }

        var retained = await LoadSnapshotRequestAsync(pair.Source, retainedUser, ct);
        await using (var fresh = await LoopbackSyncPeer.ConnectAsync(listener.Endpoint, pair.Source.Identity, pair.Target.Identity, ct))
        {
            await AuthenticateSyncAsync(fresh.Stream, pair.Target.Identity, ct);
            await SyncTcpFrameIo.WriteAsync(fresh.Stream, SyncTcpMessageType.UserSnapshotRequestBatch, SnapshotBatch(retained), ct);
            await LoopbackSyncPeer.ReadFrameAsync(fresh.Stream, SyncTcpMessageType.UserSnapshotRelayStart, ct);
            await LoopbackSyncPeer.ReadFrameAsync(fresh.Stream, SyncTcpMessageType.DeltaChunk, ct);
            await LoopbackSyncPeer.ReadFrameAsync(fresh.Stream, SyncTcpMessageType.UserSnapshotRelayEnd, ct);
            Assert.IsNull(await LoopbackSyncPeer.ReadUntilCloseAsync(fresh.Stream, ct));
        }

        UserControlOperation operation;
        using (var scope = pair.Source.Services.CreateScope())
        {
            var row = await scope.ServiceProvider.GetRequiredService<IUserControlOperationRepository>().GetByIdAsync(removal.OperationId.Value, ct);
            Assert.IsNotNull(row);
            operation = row;
        }
        await using var history = await LoopbackSyncPeer.ConnectAsync(listener.Endpoint, pair.Source.Identity, pair.Target.Identity, ct);
        await AuthenticateSyncAsync(history.Stream, pair.Target.Identity, ct);
        var batch = new UserControlOperationRequestBatch();
        batch.Requests.Add(CreateControlRequest(operation));
        await SyncTcpFrameIo.WriteAsync(history.Stream, SyncTcpMessageType.UserControlOperationRequestBatch, batch, ct);
        await LoopbackSyncPeer.ReadFrameAsync(history.Stream, SyncTcpMessageType.UserControlOperationRelayStart, ct);
        await LoopbackSyncPeer.ReadFrameAsync(history.Stream, SyncTcpMessageType.DeltaChunk, ct);
        await LoopbackSyncPeer.ReadFrameAsync(history.Stream, SyncTcpMessageType.UserControlOperationRelayEnd, ct);
    }

    [TestMethod]
    [Timeout(ThreeDeviceTestTimeoutMilliseconds)]
    [TestCategory("Integration")]
    public async Task RevokedOrigin_ReplayAcrossRestartCannotRestoreDeletedDataOrMembership()
    {
        await using var pair = await CreateEnrolledPairAsync();
        await RunStepAsync("pause automatic delivery", ct => PauseAutomaticDeliveryAsync(pair, ct));
        var revokedUser = (await pair.Source.GetOnlyLoginIdentityAsync()).UserId;
        var username = (await pair.Source.Endpoints.GetUserProfileInfoAsync(pair.SourceToken)).Username;
        var secret = Encoding.UTF8.GetBytes("synthetic-old-origin");
        const string deletedEntry = "Delete before revoking origin";
        const string lateEntry = "Never accepted from revoked origin";
        var below = await RunStepAsync("build the pre-cutoff snapshot",
            ct => BuildLatestUserDeltaAsync(pair.Target, pair.Source, ct: ct));
        await RunStepAsync("write the entry before revocation",
            ct => AddPasswordAsync(pair.Target, pair.TargetToken, deletedEntry, secret, ct));
        var atCutoff = await RunStepAsync("build the cutoff snapshot",
            ct => BuildLatestUserDeltaAsync(pair.Target, pair.Source, ct: ct));
        await RunStepAsync("deliver and merge the cutoff snapshot", async ct =>
        {
            var hello = await AcceptHelloAsync(pair.Target, pair.Source, ct);
            await pair.Source.ProtocolHandler.PushDeltaAsync(SingleDeltaAsync(atCutoff), hello, ct);
        });
        var passwords = await pair.Source.Endpoints.GetSavedPasswordsAsync(pair.SourceToken);
        await RunStepAsync("delete the entry before device removal",
            ct => pair.Source.Endpoints.RemovePasswordsAsync(pair.SourceToken, [passwords.Passwords.Single(p => p.Name == deletedEntry).Id], ct));
        var keyEpoch = (await pair.Source.GetOnlyCanonicalCheckpointAsync())!.KeyEpoch;
        // Keep the old device authenticated through a different profile after revocation.
        const string retainedName = "keep_other_route";
        var retained = await RunStepAsync("register a separate profile to retain the authenticated route",
            ct => pair.Source.Endpoints.RegisterAsync(pair.Source.CreateRegistrationRequest(retainedName), ct));
        await RunStepAsync("enable synchronization for the separate profile", ct => pair.Source.EnableSyncAsync(retained, ct));
        var secondCode = await RunStepAsync("start enrollment for the separate profile",
            ct => pair.Target.Endpoints.StartDeviceEnrollmentAsync(ct));
        await RunStepAsync("enroll the separate profile on the old device",
            ct => pair.Source.Endpoints.AddDeviceByCodeAsync(retained, secondCode.Code, ct));
        await RunStepAsync("sign in and enable the retained route on the old device", async ct =>
        {
            var remoteToken = await pair.Target.Endpoints.LoginAsync(pair.Target.CreateLoginRequest(retainedName), ct);
            await pair.Target.EnableSyncAsync(remoteToken, ct);
        });
        await RunStepAsync("pause automatic delivery", ct => PauseAutomaticDeliveryAsync(pair, ct));
        await RunStepAsync("write the post-cutoff entry on the old origin",
            ct => AddPasswordAsync(pair.Target, pair.TargetToken, lateEntry, secret, ct));
        var above = await RunStepAsync("capture the post-cutoff snapshot",
            ct => BuildLatestUserDeltaAsync(pair.Target, pair.Source, revokedUser, ct));
        Assert.IsTrue(below.SnapshotOriginRevision < atCutoff.SnapshotOriginRevision);
        Assert.IsTrue(atCutoff.SnapshotOriginRevision < above.SnapshotOriginRevision);
        Assert.IsFalse(await HasPasswordAsync(pair.Source, pair.SourceToken, lateEntry));

        var removed = await RunStepAsync("revoke the old origin",
            ct => pair.Source.Endpoints.DisconnectUserDeviceAsync(pair.SourceToken, pair.Target.Identity.LocalDeviceId,
                Encoding.UTF8.GetBytes("P@ssw0rd12345678"), ct));
        Assert.IsTrue(removed.Removed);
        await using var restarted = await RunStepAsync("restart the receiving device", _ => pair.Source.RestartAsync());
        var token = await RunStepAsync("sign in after restart", async ct =>
        {
            var login = await restarted.Endpoints.LoginAsync(restarted.CreateLoginRequest(username), ct);
            await restarted.EnableSyncAsync(login, ct);
            return login;
        });

        await using var replacement = await ProductionSyncTestHost.CreateAsync();
        restarted.ConnectTo(replacement);
        replacement.ConnectTo(restarted);
        var replacementToken = await RunStepAsync("enroll the replacement origin", async ct =>
        {
            var code = await replacement.Endpoints.StartDeviceEnrollmentAsync(ct);
            await restarted.Endpoints.AddDeviceByCodeAsync(token, code.Code, ct);
            return await replacement.Endpoints.LoginAsync(replacement.CreateLoginRequest(username), ct);
        });
        Assert.AreNotEqual(pair.Target.Identity.OriginInstanceId, replacement.Identity.OriginInstanceId);

        foreach (var replay in new[] { below, atCutoff, above })
        {
            await RunStepAsync($"replay old origin revision {replay.SnapshotOriginRevision}", async ct =>
            {
                var hello = await AcceptHelloAsync(pair.Target, restarted, ct);
                try
                {
                    await restarted.ProtocolHandler.PushDeltaAsync(SingleDeltaAsync(replay), hello, ct);
                }
                catch (SyncProtocolException ex) when (ex.StatusCode is SyncProtocolStatusCode.PermissionDenied or SyncProtocolStatusCode.InvalidArgument)
                {
                }
            });
        }

        Assert.IsFalse(await HasPasswordAsync(restarted, token, deletedEntry));
        Assert.IsFalse(await HasPasswordAsync(restarted, token, lateEntry));
        Assert.IsFalse(await HasPasswordAsync(replacement, replacementToken, deletedEntry));
        Assert.IsFalse(await HasPasswordAsync(replacement, replacementToken, lateEntry));
        using var scope = restarted.Services.CreateScope();
        var services = scope.ServiceProvider;
        var membership = services.GetRequiredService<IUserMembershipAuthorizationRepository>();
        Assert.AreEqual(0, (await membership.ListActiveForDeviceAsync(revokedUser, pair.Target.Identity.LocalDeviceId)).Count);
        Assert.IsTrue((await membership.ListActiveForDeviceAsync(revokedUser, replacement.Identity.LocalDeviceId)).Count > 0);
        var aboveRow = await services.GetRequiredService<IUserSyncSnapshotRepository>().GetExactAsync(
            revokedUser, above.SnapshotOriginDeviceId, above.SnapshotOriginInstanceId, keyEpoch, above.SnapshotOriginRevision);
        Assert.IsNull(aboveRow, "A post-cutoff snapshot must not enter the durable inbox.");
    }

    private static async Task<Guid> GetSecondUserAsync(ProductionSyncTestHost host, Guid otherUser)
    {
        using var scope = host.Services.CreateScope();
        return (await scope.ServiceProvider.GetRequiredService<IUserRepository>().ListUserIdsAsync()).Single(id => id != otherUser);
    }

    private static async Task<UserSnapshotRequest> LoadSnapshotRequestAsync(ProductionSyncTestHost host, Guid userId, CancellationToken ct)
    {
        using var scope = host.Services.CreateScope();
        var services = scope.ServiceProvider;
        var user = await services.GetRequiredService<IUserRepository>().GetByIdAsync(userId, ct);
        Assert.IsNotNull(user);
        return CreateSnapshotRequest(await services.GetRequiredService<IUserSnapshotPublisherService>().GetOrCreateAsync(user, ct));
    }

    private static UserSnapshotRequestBatch SnapshotBatch(UserSnapshotRequest request)
    {
        var batch = new UserSnapshotRequestBatch();
        batch.Requests.Add(request);
        return batch;
    }

    private static async Task AssertSnapshotRelayDeniedAsync(Stream stream, UserSnapshotRequest request, CancellationToken ct)
    {
        await SyncTcpFrameIo.WriteAsync(stream, SyncTcpMessageType.UserSnapshotRequestBatch, SnapshotBatch(request), ct);
        var error = (await LoopbackSyncPeer.ReadFrameAsync(stream, SyncTcpMessageType.Error, ct)).Parse(SyncError.Parser);
        Assert.AreEqual(SyncProtocolStatusCode.PermissionDenied.ToString(), error.Code);
        Assert.IsNull(await LoopbackSyncPeer.ReadUntilCloseAsync(stream, ct));
    }

    [TestMethod]
    [Timeout(ThreeDeviceTestTimeoutMilliseconds)]
    [TestCategory("Integration")]
    public async Task AuthenticatedPeer_EndpointHintRequiresKnownCertificateAndSharedProfile()
    {
        await using var setup = await CreateThreeDeviceSetupAsync();
        var receiver = setup.Original;
        var sender = setup.Middle;
        var candidate = setup.NewDevice;
        await SynchronizeOneWayUntilAsync(sender, receiver, "install the third device's signed membership",
            () => HasActiveMembershipForDeviceAsync(receiver, candidate.Identity.LocalDeviceId));
        await QuiesceEndpointHintWorkersAsync(receiver, sender, candidate);

        var unknownFingerprint = Guid.NewGuid().ToString("N") + Guid.NewGuid().ToString("N");
        var hint = CreateEndpointHint(candidate);
        var unknown = hint.Clone();
        unknown.DeviceId = Guid.NewGuid().ToString("N");
        unknown.TlsCertFingerprint = unknownFingerprint;
        var wrongPin = hint.Clone();
        wrongPin.TlsCertFingerprint = unknownFingerprint;

        await InjectEndpointHintAsync(receiver, sender, candidate, unknown);
        Assert.IsFalse(receiver.EndpointsCache.TryGetByFingerprint(unknownFingerprint, out _));
        await InjectEndpointHintAsync(receiver, sender, candidate, wrongPin);
        Assert.IsFalse(receiver.EndpointsCache.TryGetByFingerprint(candidate.Identity.FingerprintHex, out _));
        Assert.IsFalse(receiver.IsOnline(candidate));

        await InjectEndpointHintAsync(receiver, sender, candidate, hint);
        Assert.IsTrue(receiver.EndpointsCache.TryGetByFingerprint(candidate.Identity.FingerprintHex, out var learned));
        Assert.IsNotNull(learned);
        Assert.AreEqual(hint.Port, learned.Port);
    }

    [TestMethod]
    [Timeout(ThreeDeviceTestTimeoutMilliseconds)]
    [TestCategory("Integration")]
    public async Task AuthenticatedPeer_EndpointHintCannotAdvertiseADeviceKnownOnlyThroughAnotherProfile()
    {
        await using var pair = await CreateEnrolledPairAsync();
        await using var candidate = await ProductionSyncTestHost.CreateAsync();
        var receiver = pair.Source;
        var sender = pair.Target;
        receiver.ConnectTo(candidate);
        candidate.ConnectTo(receiver);
        var sharedUser = (await receiver.GetOnlyLoginIdentityAsync()).UserId;

        await RunStepAsync("enroll the candidate in a separate profile", async ct =>
        {
            var token = await receiver.Endpoints.RegisterAsync(receiver.CreateRegistrationRequest("private_hint_route"), ct);
            await receiver.EnableSyncAsync(token, ct);
            var code = await candidate.Endpoints.StartDeviceEnrollmentAsync(ct);
            await receiver.Endpoints.AddDeviceByCodeAsync(token, code.Code, ct);
        });
        await QuiesceEndpointHintWorkersAsync(receiver, sender, candidate);

        using (var scope = receiver.Services.CreateScope())
        {
            var services = scope.ServiceProvider;
            var known = await services.GetRequiredService<IDeviceRepository>().GetByIdAsNoTrackingAsync(candidate.Identity.LocalDeviceId);
            Assert.IsNotNull(known);
            Assert.IsTrue(known.IsTrusted);
            Assert.AreEqual(candidate.Identity.FingerprintHex, known.TlsCertFingerprint);
            var links = services.GetRequiredService<IUserDeviceRepository>();
            Assert.IsFalse(await links.HasActiveLinkAsync(sharedUser, candidate.Identity.LocalDeviceId));
        }

        await InjectEndpointHintAsync(receiver, sender, candidate, CreateEndpointHint(candidate));
        Assert.IsFalse(receiver.EndpointsCache.TryGetByFingerprint(candidate.Identity.FingerprintHex, out _));
        Assert.IsFalse(receiver.IsOnline(candidate));
    }

    private static PeerEndpointHint CreateEndpointHint(ProductionSyncTestHost candidate) => new()
    {
        DeviceId = candidate.Identity.LocalDeviceId.ToString("N"),
        TlsCertFingerprint = candidate.Identity.FingerprintHex,
        Host = "127.0.0.1",
        Port = 26701
    };

    private async Task QuiesceEndpointHintWorkersAsync(
        ProductionSyncTestHost receiver, ProductionSyncTestHost sender, ProductionSyncTestHost candidate)
    {
        await RunStepAsync("quiesce endpoint discovery before injecting hints", async ct =>
        {
            foreach (var host in new[] { receiver, sender, candidate })
                host.EndpointsCache.Clear();
            await receiver.WaitForSyncIdleAsync(sender.Identity.LocalDeviceId, ct);
            await receiver.WaitForSyncIdleAsync(candidate.Identity.LocalDeviceId, ct);
            receiver.EndpointsCache.TryRemove(candidate.Identity.FingerprintHex);
            receiver.Presence.InvalidateAll("Test each hint independently.");
        });
    }

    private async Task InjectEndpointHintAsync(ProductionSyncTestHost receiver, ProductionSyncTestHost sender,
        ProductionSyncTestHost candidate, PeerEndpointHint hint)
    {
        var delivered = 0;
        receiver.Transport.MutateSnapshotInventoryReply = (fingerprint, reply) =>
        {
            if (!string.Equals(fingerprint, sender.Identity.FingerprintHex, StringComparison.OrdinalIgnoreCase))
                return;

            reply.EndpointHints.Clear();
            reply.EndpointHints.Add(hint);
            Interlocked.Increment(ref delivered);
        };
        try
        {
            await SynchronizeOneWayUntilAsync(receiver, sender, "receive scripted endpoint hint from an enrolled peer",
                () => Task.FromResult(Volatile.Read(ref delivered) > 0));
            await RunStepAsync("wait for work triggered by the endpoint hint",
                ct => receiver.WaitForSyncIdleAsync(candidate.Identity.LocalDeviceId, ct));
        }
        finally
        {
            receiver.Transport.MutateSnapshotInventoryReply = null;
        }
    }

    [TestMethod]
    [DataRow("zero")]
    [DataRow("negative")]
    [DataRow("oversized")]
    [DataRow("truncated-header")]
    [DataRow("truncated-body")]
    [DataRow("unknown-type")]
    [DataRow("out-of-order")]
    [Timeout(TestTimeoutMilliseconds)]
    [TestCategory("Integration")]
    public async Task ProductionTcp_MalformedFramesTerminateOnlyTheirConnection(string fault)
    {
        await using var pair = await CreateEnrolledPairAsync();
        await PauseAutomaticDeliveryAsync(pair);
        using var deadline = new CancellationTokenSource(StepTimeout);
        var ct = deadline.Token;
        await using var listener = await LoopbackSyncListener.StartAsync(pair.Target.Services, ct);
        pair.Target.Presence.InvalidateAll("Observe whether malformed traffic refreshes presence.");
        await using (var peer = await LoopbackSyncPeer.ConnectAsync(listener.Endpoint, pair.Target.Identity, pair.Source.Identity, ct))
        {
            var bytes = fault switch
            {
                "zero" => CreateFrameHeader(0),
                "negative" => CreateFrameHeader(-1),
                "oversized" => CreateFrameHeader(SyncConstants.MaxSyncTcpFrameBytes + 1),
                "truncated-header" => new byte[] { 0, 0 },
                "truncated-body" => CreateFrameHeader(100),
                "unknown-type" => new byte[] { 0, 0, 0, 1, 255 },
                "out-of-order" => new byte[] { 0, 0, 0, 1, (byte)SyncTcpMessageType.PushDeltaEnd },
                _ => throw new ArgumentOutOfRangeException(nameof(fault))
            };

            foreach (var value in bytes)
                await peer.Stream.WriteAsync(new[] { value }, ct);
            if (fault is "truncated-header" or "truncated-body")
                await peer.Stream.ShutdownAsync().WaitAsync(ct);
            var reply = await LoopbackSyncPeer.ReadUntilCloseAsync(peer.Stream, ct);
            if (reply is not null)
            {
                Assert.AreEqual(SyncTcpMessageType.Error, reply.Type);
                Assert.IsNull(await LoopbackSyncPeer.ReadUntilCloseAsync(peer.Stream, ct));
            }
        }

        Assert.IsFalse(pair.Target.IsOnline(pair.Source));
        await using var valid = await LoopbackSyncPeer.ConnectAsync(listener.Endpoint, pair.Target.Identity, pair.Source.Identity, ct);
        await AuthenticateSyncAsync(valid.Stream, pair.Source.Identity, ct);
        Assert.IsTrue(pair.Target.IsOnline(pair.Source));
    }

    [TestMethod]
    [Timeout(TestTimeoutMilliseconds)]
    [TestCategory("Integration")]
    public async Task ProductionTcp_PerIpLimitRejectsExcessAndReleasesAClosedSlot()
    {
        await using var pair = await CreateEnrolledPairAsync();
        await PauseAutomaticDeliveryAsync(pair);
        using var deadline = new CancellationTokenSource(StepTimeout);
        var ct = deadline.Token;
        await using var listener = await LoopbackSyncListener.StartAsync(pair.Target.Services, ct);
        var peers = new List<LoopbackSyncPeer>();
        try
        {
            for (var i = 0; i < SyncConstants.MaxSyncConnectionsPerRemoteIp; i++)
            {
                var peer = await LoopbackSyncPeer.ConnectAsync(listener.Endpoint, pair.Target.Identity, pair.Source.Identity, ct);
                peers.Add(peer);
                await AuthenticateSyncAsync(peer.Stream, pair.Source.Identity, ct);
            }

            try
            {
                await using var excess = await LoopbackSyncPeer.ConnectAsync(listener.Endpoint, pair.Target.Identity, pair.Source.Identity, ct);
                await SyncTcpFrameIo.WriteAsync(excess.Stream, SyncTcpMessageType.HelloRequest, CreateHelloRequest(pair.Source.Identity), ct);
                Assert.IsNull(await LoopbackSyncPeer.ReadUntilCloseAsync(excess.Stream, ct),
                    "The per-IP excess socket must receive no Hello reply.");
            }
            catch (Exception ex) when (IsClosedSocket(ex))
            {
                // Socket and TLS providers differ in where they report the accept-time close.
            }

            var first = peers[0];
            await first.Stream.ShutdownAsync().WaitAsync(ct);
            Assert.IsNull(await LoopbackSyncPeer.ReadUntilCloseAsync(first.Stream, ct));
            peers.RemoveAt(0);
            await first.DisposeAsync();

            // EOF may arrive just before the listener releases the slot. Retry only socket closure.
            using var recovery = CancellationTokenSource.CreateLinkedTokenSource(ct);
            recovery.CancelAfter(TimeSpan.FromSeconds(5));
            while (true)
            {
                try
                {
                    await using var recovered = await LoopbackSyncPeer.ConnectAsync(
                        listener.Endpoint, pair.Target.Identity, pair.Source.Identity, recovery.Token);
                    await SyncTcpFrameIo.WriteAsync(recovered.Stream, SyncTcpMessageType.HelloRequest, CreateHelloRequest(pair.Source.Identity), recovery.Token);
                    var frame = await LoopbackSyncPeer.ReadUntilCloseAsync(recovered.Stream, recovery.Token);
                    if (frame is not null)
                    {
                        Assert.AreEqual(SyncTcpMessageType.HelloReply, frame.Type);
                        Assert.IsTrue(frame.Parse(HelloReply.Parser).Ok);
                        break;
                    }
                }
                catch (Exception ex) when (IsClosedSocket(ex))
                {
                }

                await Task.Delay(TimeSpan.FromMilliseconds(10), recovery.Token);
            }
        }
        finally
        {
            foreach (var peer in peers)
                await peer.DisposeAsync();
        }
    }

    [TestMethod]
    [DataRow(0)]
    [DataRow(1)]
    [Timeout(TestTimeoutMilliseconds)]
    [TestCategory("Integration")]
    public async Task ProductionTcp_EnforcesDeltaCountAtExactBoundary(int overLimit)
    {
        await using var pair = await CreateEnrolledPairAsync();
        await PauseAutomaticDeliveryAsync(pair);
        var userId = (await pair.Target.GetOnlyLoginIdentityAsync()).UserId;
        var delta = CreateDeviceLinkDelta(pair, userId, isDeletion: false, out _);
        var chunk = DeltaMapping.ToProto(delta);
        using var deadline = new CancellationTokenSource(StepTimeout);
        var ct = deadline.Token;
        await using var listener = await LoopbackSyncListener.StartAsync(pair.Target.Services, ct);
        await using var peer = await LoopbackSyncPeer.ConnectAsync(listener.Endpoint, pair.Target.Identity, pair.Source.Identity, ct);

        // Fragment a coalesced, valid Hello and start across TLS writes.
        using var initial = new MemoryStream();
        await SyncTcpFrameIo.WriteAsync(initial, SyncTcpMessageType.HelloRequest, CreateHelloRequest(pair.Source.Identity), ct);
        await SyncTcpFrameIo.WriteAsync(initial, SyncTcpMessageType.PushDeltaStart, ct);
        var bytes = initial.ToArray();
        await peer.Stream.WriteAsync(bytes.AsMemory(0, 2), ct);
        await peer.Stream.WriteAsync(bytes.AsMemory(2), ct);
        var hello = (await LoopbackSyncPeer.ReadFrameAsync(peer.Stream, SyncTcpMessageType.HelloReply, ct)).Parse(HelloReply.Parser);
        Assert.IsTrue(hello.Ok);

        // Reusing the same authenticated update isolates the count limit from expensive merges.
        for (var i = 0; i < SyncConstants.MaxIncomingDeltaCountPerCall + overLimit; i++)
            await SyncTcpFrameIo.WriteAsync(peer.Stream, SyncTcpMessageType.DeltaChunk, chunk, ct);
        if (overLimit == 0)
        {
            await SyncTcpFrameIo.WriteAsync(peer.Stream, SyncTcpMessageType.PushDeltaEnd, ct);
            await LoopbackSyncPeer.ReadFrameAsync(peer.Stream, SyncTcpMessageType.Ack, ct);
        }
        else
        {
            var error = (await LoopbackSyncPeer.ReadFrameAsync(peer.Stream, SyncTcpMessageType.Error, ct)).Parse(SyncError.Parser);
            Assert.AreEqual(SyncProtocolStatusCode.ResourceExhausted.ToString(), error.Code);
        }
        Assert.IsNull(await LoopbackSyncPeer.ReadUntilCloseAsync(peer.Stream, ct));
    }

    [TestMethod]
    [Timeout(TestTimeoutMilliseconds)]
    [TestCategory("Integration")]
    public async Task ProductionTcp_StopClosesIncompleteTransferAndSameListenerCanRestart()
    {
        await using var pair = await CreateEnrolledPairAsync();
        await PauseAutomaticDeliveryAsync(pair);
        using var deadline = new CancellationTokenSource(StepTimeout);
        var ct = deadline.Token;
        await using var listener = await LoopbackSyncListener.StartAsync(pair.Target.Services, ct);
        await using var pending = await LoopbackSyncPeer.ConnectAsync(listener.Endpoint, pair.Target.Identity, pair.Source.Identity, ct);
        await AuthenticateSyncAsync(pending.Stream, pair.Source.Identity, ct);
        await SyncTcpFrameIo.WriteAsync(pending.Stream, SyncTcpMessageType.PushDeltaStart, ct);

        await listener.StopAsync(ct);
        Assert.IsNull(await LoopbackSyncPeer.ReadUntilCloseAsync(pending.Stream, ct));
        await listener.RestartAsync(ct);
        await using var next = await LoopbackSyncPeer.ConnectAsync(listener.Endpoint, pair.Target.Identity, pair.Source.Identity, ct);
        await AuthenticateSyncAsync(next.Stream, pair.Source.Identity, ct);
        await SyncTcpFrameIo.WriteAsync(next.Stream, SyncTcpMessageType.PushDeltaStart, ct);
        await SyncTcpFrameIo.WriteAsync(next.Stream, SyncTcpMessageType.PushDeltaEnd, ct);
        await LoopbackSyncPeer.ReadFrameAsync(next.Stream, SyncTcpMessageType.Ack, ct);
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    [Timeout(TestTimeoutMilliseconds)]
    [TestCategory("SecurityStress")]
    public async Task ProductionTcp_EnforcesAggregateDeltaBytes(bool exceed)
    {
        LoopbackSyncPeer.RequireStressTestsEnabled();
        await using var pair = await CreateEnrolledPairAsync();
        await PauseAutomaticDeliveryAsync(pair);
        var userId = (await pair.Target.GetOnlyLoginIdentityAsync()).UserId;
        var delta = CreateDeviceLinkDelta(pair, userId, isDeletion: false, out _, SyncConstants.MaxIncomingDeltaPayloadBytes);
        Assert.AreEqual(SyncConstants.MaxIncomingDeltaPayloadBytes, delta.Payload.Length);
        var chunk = DeltaMapping.ToProto(delta);
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var ct = deadline.Token;
        await using var listener = await LoopbackSyncListener.StartAsync(pair.Target.Services, ct);
        await using var peer = await LoopbackSyncPeer.ConnectAsync(listener.Endpoint, pair.Target.Identity, pair.Source.Identity, ct);
        await AuthenticateSyncAsync(peer.Stream, pair.Source.Identity, ct);
        await SyncTcpFrameIo.WriteAsync(peer.Stream, SyncTcpMessageType.PushDeltaStart, ct);
        var count = Math.DivRem(SyncConstants.MaxIncomingDeltaTotalBytesPerCall, delta.Payload.Length, out var remainder);
        Assert.AreEqual(0, remainder);
        for (var i = 0; i < count; i++)
            await SyncTcpFrameIo.WriteAsync(peer.Stream, SyncTcpMessageType.DeltaChunk, chunk, ct);
        if (exceed)
        {
            var extra = DeltaMapping.ToProto(CreateDeviceLinkDelta(pair, userId, isDeletion: false, out _));
            await SyncTcpFrameIo.WriteAsync(peer.Stream, SyncTcpMessageType.DeltaChunk, extra, ct);
            var error = (await LoopbackSyncPeer.ReadFrameAsync(peer.Stream, SyncTcpMessageType.Error, ct)).Parse(SyncError.Parser);
            Assert.AreEqual(SyncProtocolStatusCode.ResourceExhausted.ToString(), error.Code);
        }
        else
        {
            await SyncTcpFrameIo.WriteAsync(peer.Stream, SyncTcpMessageType.PushDeltaEnd, ct);
            await LoopbackSyncPeer.ReadFrameAsync(peer.Stream, SyncTcpMessageType.Ack, ct);
        }
    }

    [TestMethod]
    [Timeout(TestTimeoutMilliseconds)]
    [TestCategory("SecurityStress")]
    public async Task ProductionTcp_GlobalLimitAppliesAcrossDistinctLoopbackSources()
    {
        LoopbackSyncPeer.RequireStressTestsEnabled();
        await using var pair = await CreateEnrolledPairAsync();
        await PauseAutomaticDeliveryAsync(pair);
        using var deadline = new CancellationTokenSource(StepTimeout);
        var ct = deadline.Token;
        await using var listener = await LoopbackSyncListener.StartAsync(pair.Target.Services, ct);
        var peers = new List<LoopbackSyncPeer>();
        try
        {
            for (var i = 0; i < SyncConstants.MaxConcurrentSyncConnections; i++)
            {
                var peer = await LoopbackSyncPeer.ConnectAsync(listener.Endpoint, pair.Target.Identity, pair.Source.Identity,
                    ct, IPAddress.Parse($"127.0.0.{i + 2}"));
                peers.Add(peer);
                await AuthenticateSyncAsync(peer.Stream, pair.Source.Identity, ct);
            }
            // An already-tested source address uses one IP slot, so only the global cap blocks it.
            try
            {
                await using var excess = await LoopbackSyncPeer.ConnectAsync(listener.Endpoint, pair.Target.Identity,
                    pair.Source.Identity, ct, IPAddress.Parse("127.0.0.2"));
                await SyncTcpFrameIo.WriteAsync(excess.Stream, SyncTcpMessageType.HelloRequest, CreateHelloRequest(pair.Source.Identity), ct);
                Assert.IsNull(await LoopbackSyncPeer.ReadUntilCloseAsync(excess.Stream, ct));
            }
            catch (Exception ex) when (IsClosedSocket(ex))
            {
            }
        }
        catch (SocketException ex) when (ex.SocketErrorCode == SocketError.AddressNotAvailable)
        {
            Assert.Inconclusive("This runner does not provide multiple loopback source addresses.");
        }
        finally
        {
            foreach (var peer in peers)
                await peer.DisposeAsync();
        }
    }

    private static bool IsClosedSocket(Exception ex) => ex is AuthenticationException or IOException or SocketException;

    private static byte[] CreateFrameHeader(int length)
    {
        var bytes = new byte[sizeof(int)];
        BinaryPrimitives.WriteInt32BigEndian(bytes, length);
        return bytes;
    }
}
