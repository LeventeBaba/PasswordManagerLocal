using Google.Protobuf;
using PasswordManagerLocal.Common.Backend.Abstractions.Services;
using PasswordManagerLocal.Common.Backend.Abstractions.Sync.Presence;
using PasswordManagerLocal.Common.Backend.Constants;
using PasswordManagerLocal.Common.Backend.Sync;
using PasswordManagerLocal.Common.Backend.Sync.Tcp;
using PasswordManagerLocal.Common.Backend.Sync.Presence;
using PasswordManagerLocal.Common.Backend.Sync.Discovery;
using PasswordManagerLocal.Common.Backend.Security;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

using PasswordManagerLocal.Common.Backend.Utils;

using PasswordManagerLocal.Common.Backend.Diagnostics;

namespace PasswordManagerLocal.Common.Backend.Services;

public sealed class TcpSyncClientService : ISyncTransportClientService
{
    private readonly IDeviceIdentityService _identity;
    private readonly ISyncDeviceIdentityService _syncDeviceIdentities;
    private readonly IDevicePresenceRegistry _presenceRegistry;

    public TcpSyncClientService(
        IDeviceIdentityService identity,
        ISyncDeviceIdentityService syncDeviceIdentities,
        IDevicePresenceRegistry presenceRegistry)
    {
        _identity = identity ?? throw new ArgumentNullException(nameof(identity));
        _syncDeviceIdentities = syncDeviceIdentities ?? throw new ArgumentNullException(nameof(syncDeviceIdentities));
        _presenceRegistry = presenceRegistry ?? throw new ArgumentNullException(nameof(presenceRegistry));
    }


    public async Task<DevicePresenceProbeResult> ProbeAsync(
        string host,
        int port,
        string serverFingerprintHex,
        string expectedDeviceId,
        byte[] expectedSignPublicKey,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(host) || port <= 0 ||
            string.IsNullOrWhiteSpace(serverFingerprintHex) ||
            string.IsNullOrWhiteSpace(expectedDeviceId) ||
            expectedSignPublicKey is null || expectedSignPublicKey.Length == 0)
        {
            return DevicePresenceProbeResult.Failed(DevicePresenceFailureKind.EndpointUnavailable);
        }

        try
        {
            await using var connection = await ConnectAsync(host, port, serverFingerprintHex, ct);
            var hello = await SendHelloAsync(connection.Stream, ct);
            if (!IsExpectedPeerIdentity(hello, expectedDeviceId, expectedSignPublicKey))
            {
                BackendDebugLog.Debug(
                    $"Authenticated presence probe rejected the peer identity returned after TLS. Target={host}:{port}, FingerprintPrefix={FingerprintPrefix(serverFingerprintHex)}.",
                    "Presence");
                return DevicePresenceProbeResult.Failed(DevicePresenceFailureKind.PeerIdentityMismatch);
            }

            return DevicePresenceProbeResult.Success;
        }
        catch (AuthenticationException)
        {
            BackendDebugLog.DebugRateLimited(
                $"presence-tls-mismatch:{FingerprintUtil.NormalizeOrEmpty(serverFingerprintHex)}",
                TimeSpan.FromSeconds(10),
                $"Authenticated presence probe failed TLS or certificate pin validation. Target={host}:{port}, FingerprintPrefix={FingerprintPrefix(serverFingerprintHex)}.",
                "Presence");
            return DevicePresenceProbeResult.Failed(DevicePresenceFailureKind.TlsOrFingerprintMismatch);
        }
        catch (CryptographicException)
        {
            BackendDebugLog.DebugRateLimited(
                $"presence-crypto-mismatch:{FingerprintUtil.NormalizeOrEmpty(serverFingerprintHex)}",
                TimeSpan.FromSeconds(10),
                $"Authenticated presence probe failed cryptographic peer validation. Target={host}:{port}, FingerprintPrefix={FingerprintPrefix(serverFingerprintHex)}.",
                "Presence");
            return DevicePresenceProbeResult.Failed(DevicePresenceFailureKind.TlsOrFingerprintMismatch);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            return DevicePresenceProbeResult.Failed(DevicePresenceFailureKind.Timeout);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (SocketException)
        {
            return DevicePresenceProbeResult.Failed(DevicePresenceFailureKind.Unreachable);
        }
        catch (IOException)
        {
            return DevicePresenceProbeResult.Failed(DevicePresenceFailureKind.Unreachable);
        }
        catch (InvalidDataException)
        {
            return DevicePresenceProbeResult.Failed(DevicePresenceFailureKind.ProtocolUnavailable);
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException)
        {
            return DevicePresenceProbeResult.Failed(DevicePresenceFailureKind.InvalidResponse);
        }
    }


    public async Task<bool> SendDeltasAsync(string host, int port, string serverFingerprintHex, IEnumerable<NetworkDelta> deltas, CancellationToken ct = default)
    {
        var list = deltas.ToList();
        if (list.Count == 0)
            return true;

        if (list.Count > SyncConstants.MaxIncomingDeltaCountPerCall || list.Sum(delta => (long)delta.Payload.Length) > SyncConstants.MaxIncomingDeltaTotalBytesPerCall)
            return false;

        try
        {
            await using var connection = await ConnectAsync(host, port, serverFingerprintHex, ct);

            var hello = await SendHelloAsync(connection.Stream, ct);
            RefreshOutgoingPresence(host, port, serverFingerprintHex, hello);

            await WriteFrameAsync(connection.Stream, SyncTcpMessageType.PushDeltaStart, ct);

            foreach (var delta in list.OrderBy(d => d.Ts))
                await WriteFrameAsync(connection.Stream, SyncTcpMessageType.DeltaChunk, DeltaMapping.ToProto(PrepareDelta(delta)), ct);

            await WriteFrameAsync(connection.Stream, SyncTcpMessageType.PushDeltaEnd, ct);

            var ackFrame = await ReadRequiredAsync(connection.Stream, SyncTcpMessageType.Ack, ct);
            var ack = ackFrame.Parse(Ack.Parser);

            if (ack.LastSyncedTs < list.Max(x => x.Ts))
                return false;

            var expectedSnapshots = list.Where(delta => delta.SnapshotOriginRevision > 0).ToArray();
            foreach (var expected in expectedSnapshots)
            {
                var receipt = ack.UserSnapshotReceipts.FirstOrDefault(candidate =>
                    Guid.TryParse(candidate.UserId, out var userId) && userId == expected.SnapshotUserId &&
                    Guid.TryParse(candidate.OriginDeviceId, out var originDeviceId) && originDeviceId == expected.SnapshotOriginDeviceId &&
                    Guid.TryParse(candidate.OriginInstanceId, out var originInstanceId) && originInstanceId == expected.SnapshotOriginInstanceId &&
                    candidate.OriginRevision == expected.SnapshotOriginRevision &&
                    CryptographicOperations.FixedTimeEquals(candidate.SnapshotHash.ToByteArray(), expected.SnapshotHash));

                if (receipt is null || !IsDurableSnapshotReceipt(receipt.State))
                    return false;
            }

            var expectedOperations = list.Where(delta => delta.ControlOperationOriginSequence > 0).ToArray();
            foreach (var expected in expectedOperations)
            {
                var receipt = ack.UserControlOperationReceipts.FirstOrDefault(candidate =>
                    Guid.TryParse(candidate.OperationId, out var operationId) && operationId == expected.ControlOperationId &&
                    Guid.TryParse(candidate.UserId, out var userId) && userId == expected.ControlOperationUserId &&
                    Guid.TryParse(candidate.OriginDeviceId, out var originDeviceId) && originDeviceId == expected.ControlOperationOriginDeviceId &&
                    Guid.TryParse(candidate.OriginInstanceId, out var originInstanceId) && originInstanceId == expected.ControlOperationOriginInstanceId &&
                    candidate.OriginSequence == expected.ControlOperationOriginSequence &&
                    CryptographicOperations.FixedTimeEquals(candidate.OperationHash.ToByteArray(), expected.ControlOperationHash));

                if (receipt is null || !IsDurableControlOperationReceipt(receipt.State))
                    return false;
            }

            return true;
        }
        catch (System.Net.Sockets.SocketException)
        {
            return false;
        }
        catch (IOException)
        {
            return false;
        }
        catch (InvalidDataException)
        {
            return false;
        }
        catch (InvalidOperationException)
        {
            return false;
        }
        catch (CryptographicException)
        {
            return false;
        }
        catch (AuthenticationException)
        {
            return false;
        }
        catch (ArgumentException)
        {
            return false;
        }
        catch (OperationCanceledException)
        {
            return false;
        }
    }


    public async Task<UserSnapshotInventoryExchangeReply> ExchangeUserSnapshotInventoryAsync(
        string host,
        int port,
        string serverFingerprintHex,
        UserSnapshotInventoryExchangeRequest request,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (request.Users.Count > SyncConstants.MaxUserSnapshotInventoryUsers ||
            request.Users.Sum(user => user.Revisions.Count) > SyncConstants.MaxUserSnapshotInventoryEntries)
        {
            throw new InvalidDataException("The user snapshot inventory exceeds the protocol limits.");
        }

        await using var connection = await ConnectAsync(host, port, serverFingerprintHex, ct);
        var hello = await SendHelloAsync(connection.Stream, ct);
        RefreshOutgoingPresence(host, port, serverFingerprintHex, hello);
        await WriteFrameAsync(connection.Stream, SyncTcpMessageType.UserSnapshotInventoryRequest, request, ct);
        var frame = await ReadRequiredAsync(connection.Stream, SyncTcpMessageType.UserSnapshotInventoryReply, ct);
        var reply = frame.Parse(UserSnapshotInventoryExchangeReply.Parser);
        if (reply.Users.Count > SyncConstants.MaxUserSnapshotInventoryUsers ||
            reply.Users.Sum(user => user.Revisions.Count) > SyncConstants.MaxUserSnapshotInventoryEntries ||
            reply.RequestedSnapshots.Count > SyncConstants.MaxUserSnapshotRequestsPerCall)
        {
            throw new InvalidDataException("The remote user snapshot inventory exceeds the protocol limits.");
        }

        return reply;
    }


    public async Task<IReadOnlyList<NetworkDelta>> RequestUserSnapshotsAsync(
        string host,
        int port,
        string serverFingerprintHex,
        UserSnapshotRequestBatch request,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (request.Requests.Count > SyncConstants.MaxUserSnapshotRequestsPerCall)
            throw new InvalidDataException("Too many user snapshots were requested.");

        await using var connection = await ConnectAsync(host, port, serverFingerprintHex, ct);
        var hello = await SendHelloAsync(connection.Stream, ct);
        RefreshOutgoingPresence(host, port, serverFingerprintHex, hello);
        await WriteFrameAsync(connection.Stream, SyncTcpMessageType.UserSnapshotRequestBatch, request, ct);
        await ReadRequiredAsync(connection.Stream, SyncTcpMessageType.UserSnapshotRelayStart, ct);

        var deltas = new List<NetworkDelta>();
        long totalBytes = 0;
        while (true)
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TimeSpan.FromSeconds(SyncConstants.SyncTcpIdleTimeoutSeconds));
            var frame = await SyncTcpFrameIo.ReadAsync(connection.Stream, timeout.Token)
                ?? throw new EndOfStreamException();
            if (frame.Type == SyncTcpMessageType.UserSnapshotRelayEnd)
                break;
            if (frame.Type == SyncTcpMessageType.Error)
            {
                var error = frame.Parse(SyncError.Parser);
                throw new InvalidDataException(string.IsNullOrWhiteSpace(error.Message) ? error.Code : error.Message);
            }
            if (frame.Type != SyncTcpMessageType.DeltaChunk)
                throw new InvalidDataException("Unexpected frame in the user snapshot relay stream.");

            if (deltas.Count >= SyncConstants.MaxUserSnapshotRequestsPerCall)
                throw new InvalidDataException("The remote user snapshot relay stream contains too many deltas.");

            var delta = DeltaMapping.FromProto(frame.Parse(DeltaChunk.Parser));
            totalBytes += delta.Payload.Length;
            if (totalBytes > SyncConstants.MaxIncomingDeltaTotalBytesPerCall)
                throw new InvalidDataException("The remote user snapshot relay stream is too large.");
            deltas.Add(delta);
        }

        return deltas;
    }


    public async Task<UserControlOperationInventoryExchangeReply> ExchangeUserControlOperationInventoryAsync(
        string host,
        int port,
        string serverFingerprintHex,
        UserControlOperationInventoryExchangeRequest request,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (request.Users.Count > SyncConstants.MaxUserControlInventoryUsers ||
            request.Users.Sum(user => user.Operations.Count) > SyncConstants.MaxUserControlInventoryEntries)
        {
            throw new InvalidDataException("The control-operation inventory exceeds the protocol limits.");
        }

        await using var connection = await ConnectAsync(host, port, serverFingerprintHex, ct);
        var hello = await SendHelloAsync(connection.Stream, ct);
        RefreshOutgoingPresence(host, port, serverFingerprintHex, hello);
        await WriteFrameAsync(connection.Stream, SyncTcpMessageType.UserControlOperationInventoryRequest, request, ct);
        var frame = await ReadRequiredAsync(connection.Stream, SyncTcpMessageType.UserControlOperationInventoryReply, ct);
        var reply = frame.Parse(UserControlOperationInventoryExchangeReply.Parser);
        if (reply.Users.Count > SyncConstants.MaxUserControlInventoryUsers ||
            reply.Users.Sum(user => user.Operations.Count) > SyncConstants.MaxUserControlInventoryEntries ||
            reply.RequestedOperations.Count > SyncConstants.MaxUserControlRequestsPerCall)
        {
            throw new InvalidDataException("The remote control-operation inventory exceeds the protocol limits.");
        }

        return reply;
    }

    public async Task<IReadOnlyList<NetworkDelta>> RequestUserControlOperationsAsync(
        string host,
        int port,
        string serverFingerprintHex,
        UserControlOperationRequestBatch request,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (request.Requests.Count > SyncConstants.MaxUserControlRequestsPerCall)
            throw new InvalidDataException("Too many control operations were requested.");

        await using var connection = await ConnectAsync(host, port, serverFingerprintHex, ct);
        var hello = await SendHelloAsync(connection.Stream, ct);
        RefreshOutgoingPresence(host, port, serverFingerprintHex, hello);
        await WriteFrameAsync(connection.Stream, SyncTcpMessageType.UserControlOperationRequestBatch, request, ct);
        await ReadRequiredAsync(connection.Stream, SyncTcpMessageType.UserControlOperationRelayStart, ct);

        var deltas = new List<NetworkDelta>();
        long totalBytes = 0;
        while (true)
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TimeSpan.FromSeconds(SyncConstants.SyncTcpIdleTimeoutSeconds));
            var frame = await SyncTcpFrameIo.ReadAsync(connection.Stream, timeout.Token)
                ?? throw new EndOfStreamException();
            if (frame.Type == SyncTcpMessageType.UserControlOperationRelayEnd)
                break;
            if (frame.Type == SyncTcpMessageType.Error)
            {
                var error = frame.Parse(SyncError.Parser);
                throw new InvalidDataException(string.IsNullOrWhiteSpace(error.Message) ? error.Code : error.Message);
            }
            if (frame.Type != SyncTcpMessageType.DeltaChunk)
                throw new InvalidDataException("Unexpected frame in the control-operation relay stream.");
            if (deltas.Count >= SyncConstants.MaxUserControlRequestsPerCall)
                throw new InvalidDataException("The remote control-operation relay stream contains too many deltas.");

            var delta = DeltaMapping.FromProto(frame.Parse(DeltaChunk.Parser));
            totalBytes += delta.Payload.Length;
            if (totalBytes > SyncConstants.MaxIncomingDeltaTotalBytesPerCall)
                throw new InvalidDataException("The remote control-operation relay stream is too large.");
            deltas.Add(delta);
        }

        return deltas;
    }

    private async Task<HelloReply> SendHelloAsync(Stream stream, CancellationToken ct)
    {
        await WriteFrameAsync(stream, SyncTcpMessageType.HelloRequest, new HelloRequest
        {
            DeviceId = _identity.DeviceIdHex,
            SignPub = ByteString.CopyFrom(_identity.SignPublicKey),
            DatabaseVersion = DatabaseConstants.CurrentDbVersion,
            ProtocolVersion = SyncConstants.SyncProtocolVersion
        }, ct);

        var helloFrame = await ReadRequiredAsync(stream, SyncTcpMessageType.HelloReply, ct);
        var hello = helloFrame.Parse(HelloReply.Parser);
        if (!hello.Ok ||
            hello.ProtocolVersion != SyncConstants.SyncProtocolVersion ||
            !hello.SyncAvailable ||
            string.IsNullOrWhiteSpace(hello.DeviceId) ||
            hello.SignPub.Length == 0)
        {
            throw new InvalidDataException("The remote peer rejected the synchronization hello or did not expose the required synchronization protocol.");
        }

        return hello;
    }

    private void RefreshOutgoingPresence(
        string host,
        int port,
        string serverFingerprintHex,
        HelloReply hello)
    {
        if (!_syncDeviceIdentities.TryGetByFingerprint(serverFingerprintHex, out var device) ||
            device is null ||
            !IsExpectedPeerIdentity(
                hello,
                Convert.ToHexString(Hashing.SHA256Hash(device.SignPublicKey)),
                device.SignPublicKey))
        {
            BackendDebugLog.DebugRateLimited(
                $"outgoing-presence-identity-mismatch:{FingerprintUtil.NormalizeOrEmpty(serverFingerprintHex)}",
                TimeSpan.FromSeconds(10),
                $"A successful outgoing TLS/hello session was not used for presence because the peer identity did not match the cached trusted device. FingerprintPrefix={FingerprintPrefix(serverFingerprintHex)}.",
                "Presence");
            return;
        }

        _presenceRegistry.RefreshAuthenticated(
            serverFingerprintHex,
            new DiscoveredDeviceEndpoint
            {
                Host = host,
                Port = port,
                TlsCertFingerprint = serverFingerprintHex
            },
            DevicePresenceObservationSource.OutgoingSync);
        BackendDebugLog.Debug(
            $"Authenticated device presence was refreshed through outgoing synchronization. TargetDeviceId={device.Id:N}, FingerprintPrefix={FingerprintPrefix(serverFingerprintHex)}.",
            "Presence");
    }

    private static bool IsExpectedPeerIdentity(
        HelloReply hello,
        string expectedDeviceId,
        byte[] expectedSignPublicKey) =>
        hello.SyncAvailable &&
        hello.ProtocolVersion == SyncConstants.SyncProtocolVersion &&
        string.Equals(hello.DeviceId, expectedDeviceId, StringComparison.OrdinalIgnoreCase) &&
        CryptographicOperations.FixedTimeEquals(hello.SignPub.ToByteArray(), expectedSignPublicKey);

    private static string FingerprintPrefix(string fingerprint)
    {
        var normalized = FingerprintUtil.NormalizeOrEmpty(fingerprint);
        return normalized[..Math.Min(16, normalized.Length)];
    }


    private bool IsDurableSnapshotReceipt(UserSnapshotReceiptStateProto state) =>
        state is
            UserSnapshotReceiptStateProto.UserSnapshotReceiptStoredPending or
            UserSnapshotReceiptStateProto.UserSnapshotReceiptReplacedOlderPending or
            UserSnapshotReceiptStateProto.UserSnapshotReceiptAlreadyStored or
            UserSnapshotReceiptStateProto.UserSnapshotReceiptStoredMergedReceipt or
            UserSnapshotReceiptStateProto.UserSnapshotReceiptMergedImmediately or
            UserSnapshotReceiptStateProto.UserSnapshotReceiptObsoleteRevision or
            UserSnapshotReceiptStateProto.UserSnapshotReceiptRejectedAccountDeleted;


    private bool IsDurableControlOperationReceipt(UserControlOperationReceiptStateProto state) =>
        state is
            UserControlOperationReceiptStateProto.UserControlOperationReceiptStoredPending or
            UserControlOperationReceiptStateProto.UserControlOperationReceiptAlreadyStored or
            UserControlOperationReceiptStateProto.UserControlOperationReceiptApplied or
            UserControlOperationReceiptStateProto.UserControlOperationReceiptObsolete;


    public async Task<GetDeviceEnrollmentInfoReply> GetDeviceEnrollmentInfoAsync(string host, int port, string serverFingerprintHex, GetDeviceEnrollmentInfoRequest request, CancellationToken ct = default)
    {
        // Compact enrollment codes intentionally carry a 16-byte (32 hex character)
        // certificate fingerprint prefix. This initial, code-proof-authenticated identity lookup is
        // the only transport operation allowed to pin by that prefix. The returned enrollment
        // identity contains the full fingerprint, which is used for the snapshot transfer and all
        // later synchronization and presence traffic.
        await using var connection = await ConnectAsync(
            host,
            port,
            serverFingerprintHex,
            ct,
            allowEnrollmentFingerprintPrefix: true);

        await WriteFrameAsync(connection.Stream, SyncTcpMessageType.GetDeviceEnrollmentInfoRequest, request, ct);
        var frame = await ReadRequiredAsync(connection.Stream, SyncTcpMessageType.GetDeviceEnrollmentInfoReply, ct);
        return frame.Parse(GetDeviceEnrollmentInfoReply.Parser);
    }


    public async Task<CompleteDeviceEnrollmentReply> CompleteDeviceEnrollmentStreamAsync(string host, int port, string serverFingerprintHex, IAsyncEnumerable<CompleteDeviceEnrollmentChunk> chunks, CancellationToken ct = default)
    {
        await using var connection = await ConnectAsync(host, port, serverFingerprintHex, ct);

        await WriteFrameAsync(connection.Stream, SyncTcpMessageType.CompleteDeviceEnrollmentStart, new CompleteDeviceEnrollmentStartRequest
        {
            SourceDatabaseVersion = DatabaseConstants.CurrentDbVersion
        }, ct);

        await foreach (var chunk in chunks.WithCancellation(ct))
            await WriteFrameAsync(connection.Stream, SyncTcpMessageType.CompleteDeviceEnrollmentChunk, chunk, ct);

        await WriteFrameAsync(connection.Stream, SyncTcpMessageType.CompleteDeviceEnrollmentEnd, ct);

        var frame = await ReadRequiredAsync(connection.Stream, SyncTcpMessageType.CompleteDeviceEnrollmentReply, ct, SyncConstants.DeviceEnrollmentTransferTimeoutSeconds);
        return frame.Parse(CompleteDeviceEnrollmentReply.Parser);
    }


    private async Task<TcpSyncClientConnection> ConnectAsync(
        string host,
        int port,
        string serverFingerprintHex,
        CancellationToken ct,
        bool allowEnrollmentFingerprintPrefix = false)
    {
        var preferredSourceAddress = FindPreferredSourceAddress(host);
        var client = await ConnectTcpAsync(host, port, preferredSourceAddress, ct);

        try
        {
            client.NoDelay = true;
            client.ReceiveTimeout = SyncConstants.SyncTcpIdleTimeoutSeconds * 1000;
            client.SendTimeout = SyncConstants.SyncTcpWriteTimeoutSeconds * 1000;

            var localEndpoint = client.Client.LocalEndPoint?.ToString() ?? "unknown";
            var remoteEndpoint = client.Client.RemoteEndPoint?.ToString() ?? $"{host}:{port}";
            BackendDebugLog.Info($"TCP connection established. Local={localEndpoint}, Remote={remoteEndpoint}. Starting TLS authentication.");

            var stream = new SslStream(client.GetStream(), false);
            using var handshakeTimeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            handshakeTimeout.CancelAfter(TimeSpan.FromSeconds(SyncConstants.SyncTcpHandshakeTimeoutSeconds));

            await stream.AuthenticateAsClientAsync(new SslClientAuthenticationOptions
            {
                TargetHost = host,
                EnabledSslProtocols = SslProtocols.Tls12 | SslProtocols.Tls13,
                ClientCertificates = new X509CertificateCollection { _identity.Certificate },
                LocalCertificateSelectionCallback = (_, _, _, _, _) => _identity.Certificate,
                RemoteCertificateValidationCallback = (_, cert, _, _) => ValidatePinnedServerCertificate(
                    cert,
                    serverFingerprintHex,
                    allowEnrollmentFingerprintPrefix),
                CertificateRevocationCheckMode = X509RevocationMode.NoCheck
            }, handshakeTimeout.Token);

            var serverFingerprint = stream.RemoteCertificate is null
                ? string.Empty
                : _identity.GetFingerprintHex(new X509Certificate2(stream.RemoteCertificate));

            BackendDebugLog.Info($"TLS authentication completed. Local={localEndpoint}, Remote={remoteEndpoint}, ServerFingerprintPrefix={FingerprintUtil.Normalize(serverFingerprint)[..Math.Min(16, FingerprintUtil.Normalize(serverFingerprint).Length)]}.");
            return new TcpSyncClientConnection(client, stream);
        }
        catch
        {
            client.Dispose();
            throw;
        }
    }


    private async Task<TcpClient> ConnectTcpAsync(string host, int port, IPAddress? preferredSourceAddress, CancellationToken ct)
    {
        var attempts = preferredSourceAddress is null
            ? new IPAddress?[] { null }
            : new IPAddress?[] { preferredSourceAddress, null };

        var errors = new List<Exception>();
        var perAttemptTimeout = TimeSpan.FromSeconds(Math.Max(2, SyncConstants.DeviceEnrollmentConnectTimeoutSeconds / attempts.Length));

        foreach (var sourceAddress in attempts)
        {
            ct.ThrowIfCancellationRequested();

            var client = CreateTcpClient(host, sourceAddress);
            try
            {
                if (sourceAddress is not null)
                    client.Client.Bind(new IPEndPoint(sourceAddress, 0));

                var sourceText = sourceAddress?.ToString() ?? "OS-selected";
                BackendDebugLog.Info($"TCP connection attempt started. Source={sourceText}, Target={host}:{port}.");

                using var connectTimeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
                connectTimeout.CancelAfter(perAttemptTimeout);
                await client.ConnectAsync(host, port, connectTimeout.Token);
                return client;
            }
            catch (OperationCanceledException ex) when (!ct.IsCancellationRequested)
            {
                errors.Add(ex);
                BackendDebugLog.Error($"TCP connection attempt timed out. Source={sourceAddress?.ToString() ?? "OS-selected"}, Target={host}:{port}.", ex);
                client.Dispose();
            }
            catch (Exception ex) when (ex is SocketException or IOException or ArgumentException or InvalidOperationException)
            {
                errors.Add(ex);
                BackendDebugLog.Error($"TCP connection attempt failed. Source={sourceAddress?.ToString() ?? "OS-selected"}, Target={host}:{port}: {ex.Message}", ex);
                client.Dispose();
            }
        }

        throw new IOException(
            $"No TCP route could connect to {host}:{port}. Tried source addresses: {string.Join(", ", attempts.Select(address => address?.ToString() ?? "OS-selected"))}.",
            errors.LastOrDefault());
    }


    private TcpClient CreateTcpClient(string host, IPAddress? sourceAddress)
    {
        if (sourceAddress is not null)
            return new TcpClient(sourceAddress.AddressFamily);

        if (IPAddress.TryParse(host, out var remoteAddress))
            return new TcpClient(remoteAddress.AddressFamily);

        return new TcpClient();
    }


    private IPAddress? FindPreferredSourceAddress(string host)
    {
        if (!IPAddress.TryParse(host, out var remoteAddress) || remoteAddress.AddressFamily != AddressFamily.InterNetwork)
            return null;

        try
        {
            return NetworkInterface.GetAllNetworkInterfaces()
                .Where(LocalNetworkInterfaceUtil.IsOperationalForLocalNetwork)
                .Where(networkInterface => networkInterface.NetworkInterfaceType is not NetworkInterfaceType.Loopback and not NetworkInterfaceType.Tunnel)
                .SelectMany(networkInterface =>
                {
                    var properties = networkInterface.GetIPProperties();
                    var hasGateway = properties.GatewayAddresses.Any(gateway => IsUsableIpv4(gateway.Address));
                    var isVirtual = IsVirtualAdapter(networkInterface);

                    return properties.UnicastAddresses
                        .Where(address => address.Address.AddressFamily == AddressFamily.InterNetwork && address.IPv4Mask is not null)
                        .Where(address => IsUsableIpv4(address.Address))
                        .Select(address => new
                        {
                            Address = address.Address,
                            SameSubnet = IsInSameIpv4Subnet(remoteAddress, address.Address, address.IPv4Mask!),
                            Priority = (isVirtual ? 0 : 10000) +
                                       (hasGateway ? 3000 : 0) +
                                       GetInterfacePriority(networkInterface.NetworkInterfaceType)
                        });
                })
                .Where(candidate => candidate.SameSubnet)
                .OrderByDescending(candidate => candidate.Priority)
                .ThenBy(candidate => candidate.Address.ToString(), StringComparer.Ordinal)
                .Select(candidate => candidate.Address)
                .FirstOrDefault();
        }
        catch
        {
            return null;
        }
    }


    private bool IsInSameIpv4Subnet(IPAddress remoteAddress, IPAddress localAddress, IPAddress mask)
    {
        var remoteBytes = remoteAddress.GetAddressBytes();
        var localBytes = localAddress.GetAddressBytes();
        var maskBytes = mask.GetAddressBytes();

        if (remoteBytes.Length != 4 || localBytes.Length != 4 || maskBytes.Length != 4)
            return false;

        for (var index = 0; index < 4; index++)
        {
            if ((remoteBytes[index] & maskBytes[index]) != (localBytes[index] & maskBytes[index]))
                return false;
        }

        return true;
    }


    private bool IsUsableIpv4(IPAddress address)
    {
        if (address.AddressFamily != AddressFamily.InterNetwork || IPAddress.IsLoopback(address) || address.Equals(IPAddress.Any) || address.Equals(IPAddress.Broadcast))
            return false;

        var bytes = address.GetAddressBytes();
        return bytes.Length == 4 && !(bytes[0] == 169 && bytes[1] == 254);
    }


    private bool IsVirtualAdapter(NetworkInterface networkInterface)
    {
        var text = $"{networkInterface.Name} {networkInterface.Description}".ToLowerInvariant();
        return text.Contains("virtual") ||
               text.Contains("vethernet") ||
               text.Contains("hyper-v") ||
               text.Contains("vmware") ||
               text.Contains("virtualbox") ||
               text.Contains("wsl") ||
               text.Contains("docker") ||
               text.Contains("vpn") ||
               text.Contains("tap") ||
               text.Contains("tunnel");
    }


    private int GetInterfacePriority(NetworkInterfaceType interfaceType) =>
        interfaceType switch
        {
            NetworkInterfaceType.Ethernet => 2500,
            NetworkInterfaceType.GigabitEthernet => 2500,
            NetworkInterfaceType.FastEthernetFx => 2500,
            NetworkInterfaceType.FastEthernetT => 2500,
            NetworkInterfaceType.Wireless80211 => 2000,
            _ => 0
        };


    private async Task<SyncTcpFrame> ReadRequiredAsync(Stream stream, SyncTcpMessageType expectedType, CancellationToken ct, int timeoutSeconds = SyncConstants.SyncTcpIdleTimeoutSeconds)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(timeoutSeconds));
        var frame = await SyncTcpFrameIo.ReadAsync(stream, timeout.Token) ?? throw new EndOfStreamException();
        if (frame.Type == SyncTcpMessageType.Error)
        {
            var error = frame.Parse(SyncError.Parser);
            throw new InvalidDataException(string.IsNullOrWhiteSpace(error.Message) ? error.Code : error.Message);
        }

        if (frame.Type != expectedType)
            throw new InvalidDataException($"Unexpected sync TCP frame type. Expected={expectedType}, Actual={frame.Type}.");

        return frame;
    }


    private async Task WriteFrameAsync(Stream stream, SyncTcpMessageType type, CancellationToken ct)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(SyncConstants.SyncTcpWriteTimeoutSeconds));
        await SyncTcpFrameIo.WriteAsync(stream, type, timeout.Token);
    }


    private async Task WriteFrameAsync(Stream stream, SyncTcpMessageType type, IMessage message, CancellationToken ct)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(SyncConstants.SyncTcpWriteTimeoutSeconds));
        await SyncTcpFrameIo.WriteAsync(stream, type, message, timeout.Token);
    }


    private bool ValidatePinnedServerCertificate(
        X509Certificate? cert,
        string serverFingerprintHex,
        bool allowEnrollmentFingerprintPrefix)
    {
        if (cert is null)
            return false;

        var fingerprint = FingerprintUtil.Normalize(_identity.GetFingerprintHex(new X509Certificate2(cert)));
        var expected = FingerprintUtil.Normalize(serverFingerprintHex);

        if (expected.Length == 64)
            return string.Equals(fingerprint, expected, StringComparison.OrdinalIgnoreCase);

        // This is not a general compatibility path. The current compact enrollment-code format
        // stores exactly the first 16 bytes of the target certificate SHA-256 fingerprint. The
        // enrollment-info request is additionally authenticated by the high-entropy enrollment
        // secret/code proof, and its response upgrades the endpoint to the complete fingerprint.
        return allowEnrollmentFingerprintPrefix &&
            expected.Length == 32 &&
            fingerprint.StartsWith(expected, StringComparison.OrdinalIgnoreCase);
    }


    private NetworkDelta PrepareDelta(NetworkDelta delta)
    {
        if (!string.Equals(delta.DeviceId, _identity.DeviceIdHex, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Delta source device id is invalid.");

        if (!delta.SignPub.SequenceEqual(_identity.SignPublicKey))
            throw new InvalidDataException("Delta signer is invalid.");

        if (string.IsNullOrWhiteSpace(delta.RecipientDeviceId))
            throw new InvalidDataException("Delta recipient is missing.");

        if (delta.EncryptionVersion != SyncConstants.SyncDeltaEncryptionVersion ||
            delta.Payload.Length == 0 ||
            delta.Payload.Length > SyncConstants.MaxIncomingDeltaPayloadBytes ||
            delta.EphemeralPublicKey.Length != SyncConstants.SyncDeltaX25519PublicKeyBytes ||
            delta.Nonce.Length != SyncConstants.SyncDeltaNonceBytes ||
            delta.Tag.Length != SyncConstants.SyncDeltaTagBytes ||
            delta.PayloadHash.Length != SyncConstants.SyncDeltaPayloadHashBytes)
            throw new InvalidDataException("Delta encryption envelope is incomplete.");

        if (delta.Sig.Length == 0)
            NetDeltaSigner.FillSignature(delta, _identity);
        else if (!NetDeltaSigner.VerifySignature(delta))
            throw new InvalidDataException("Delta signature is invalid.");

        return delta;
    }




    }
