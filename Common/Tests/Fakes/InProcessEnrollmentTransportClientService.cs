using Google.Protobuf;
using PasswordManagerLocal.Common.Backend.Abstractions.Services;
using PasswordManagerLocal.Common.Backend.Abstractions.Sync.Presence;
using PasswordManagerLocal.Common.Backend.Constants;
using PasswordManagerLocal.Common.Backend.Sync;
using PasswordManagerLocal.Common.Backend.Sync.Discovery;
using PasswordManagerLocal.Common.Backend.Sync.Presence;
using PasswordManagerLocal.Common.Backend.Sync.Tcp;
using PasswordManagerLocal.Common.Backend.Utils;
using System.Runtime.CompilerServices;
using System.Security.Authentication;
using System.Security.Cryptography;

namespace PasswordManagerLocal.Common.Tests.Fakes;

/// <summary>
/// Routes enrollment and regular synchronization transport calls directly into another backend's
/// protocol handler. This keeps protocol authentication, encrypted deltas, receipts, anti-entropy,
/// snapshot validation, and import/merge behavior inside the test boundary while deliberately
/// excluding operating-system sockets and certificates.
/// </summary>
public sealed class InProcessEnrollmentTransportClientService : ISyncTransportClientService
{
    private SyncPeerProtocolHandler? _localHandler;
    private IDeviceIdentityService? _localIdentity;
    private IDevicePresenceRegistry? _localPresenceRegistry;
    private SyncPeerProtocolHandler? _remoteHandler;
    private IDeviceIdentityService? _remoteIdentity;

    public bool ThrowAfterNextSuccessfulEnrollmentCompletion { get; set; }
    public bool TamperNextEnrollmentSnapshot { get; set; }
    public bool FailNextDeltaSend { get; set; }
    public string? ClientCertificateFingerprintOverride { get; set; }
    public string? PresentedServerCertificateFingerprintOverride { get; set; }
    public int EnrollmentInfoCalls { get; private set; }
    public int EnrollmentCompletionCalls { get; private set; }
    public int DeltaSendCalls { get; private set; }
    public int SnapshotInventoryExchangeCalls { get; private set; }
    public int SnapshotRequestCalls { get; private set; }
    public int ControlOperationInventoryExchangeCalls { get; private set; }
    public int ControlOperationRequestCalls { get; private set; }
    public int ProbeCalls { get; private set; }

    public void BindLocalBackend(
        SyncPeerProtocolHandler handler,
        IDeviceIdentityService identity,
        IDevicePresenceRegistry? presenceRegistry = null)
    {
        _localHandler = handler ?? throw new ArgumentNullException(nameof(handler));
        _localIdentity = identity ?? throw new ArgumentNullException(nameof(identity));
        _localPresenceRegistry = presenceRegistry;
    }

    public void ConnectTo(SyncPeerProtocolHandler remoteHandler, IDeviceIdentityService remoteIdentity)
    {
        _remoteHandler = remoteHandler ?? throw new ArgumentNullException(nameof(remoteHandler));
        _remoteIdentity = remoteIdentity ?? throw new ArgumentNullException(nameof(remoteIdentity));
    }

    public void Disconnect()
    {
        _remoteHandler = null;
        _remoteIdentity = null;
        ThrowAfterNextSuccessfulEnrollmentCompletion = false;
        TamperNextEnrollmentSnapshot = false;
        FailNextDeltaSend = false;
        ClientCertificateFingerprintOverride = null;
        PresentedServerCertificateFingerprintOverride = null;
    }

    public async Task<DevicePresenceProbeResult> ProbeAsync(
        string host,
        int port,
        string serverFingerprintHex,
        string expectedDeviceId,
        byte[] expectedSignPublicKey,
        CancellationToken ct = default)
    {
        ProbeCalls++;
        try
        {
            var destinationHandler = ResolveHandler(serverFingerprintHex);
            var localIdentity = GetLocalIdentity();
            var hello = await destinationHandler.HelloAsync(
                new HelloRequest
                {
                    DeviceId = localIdentity.DeviceIdHex,
                    SignPub = ByteString.CopyFrom(localIdentity.SignPublicKey),
                    DatabaseVersion = DatabaseConstants.CurrentDbVersion,
                    ProtocolVersion = SyncConstants.SyncProtocolVersion
                },
                new PeerConnectionContext
                {
                    ClientCertificateFingerprint = ClientCertificateFingerprintOverride ?? localIdentity.FingerprintHex,
                    RemoteIpAddress = "127.0.0.1"
                },
                ct);

            if (!hello.Ok ||
                hello.ProtocolVersion != SyncConstants.SyncProtocolVersion ||
                !hello.SyncAvailable)
            {
                return DevicePresenceProbeResult.Failed(DevicePresenceFailureKind.ProtocolUnavailable);
            }

            return string.Equals(hello.DeviceId, expectedDeviceId, StringComparison.OrdinalIgnoreCase) &&
                   CryptographicOperations.FixedTimeEquals(hello.SignPub.ToByteArray(), expectedSignPublicKey)
                ? DevicePresenceProbeResult.Success
                : DevicePresenceProbeResult.Failed(DevicePresenceFailureKind.PeerIdentityMismatch);
        }
        catch (AuthenticationException)
        {
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
        catch (Exception ex) when (ex is IOException or InvalidDataException or InvalidOperationException or SyncProtocolException)
        {
            return DevicePresenceProbeResult.Failed(DevicePresenceFailureKind.Unreachable);
        }
    }

    public Task<GetDeviceEnrollmentInfoReply> GetDeviceEnrollmentInfoAsync(
        string host,
        int port,
        string serverFingerprintHex,
        GetDeviceEnrollmentInfoRequest request,
        CancellationToken ct = default)
    {
        EnrollmentInfoCalls++;
        return ResolveHandler(serverFingerprintHex, allowEnrollmentFingerprintPrefix: true)
            .GetDeviceEnrollmentInfoAsync(request, ct);
    }

    public async Task<CompleteDeviceEnrollmentReply> CompleteDeviceEnrollmentStreamAsync(
        string host,
        int port,
        string serverFingerprintHex,
        IAsyncEnumerable<CompleteDeviceEnrollmentChunk> chunks,
        CancellationToken ct = default)
    {
        EnrollmentCompletionCalls++;
        var destinationHandler = ResolveHandler(serverFingerprintHex);

        var localIdentity = GetLocalIdentity();
        var stream = chunks;
        if (TamperNextEnrollmentSnapshot)
        {
            TamperNextEnrollmentSnapshot = false;
            stream = TamperFirstSnapshotByteAsync(chunks, ct);
        }

        var reply = await destinationHandler.CompleteDeviceEnrollmentStreamAsync(
            stream,
            new PeerConnectionContext
            {
                ClientCertificateFingerprint = ClientCertificateFingerprintOverride ?? localIdentity.FingerprintHex,
                RemoteIpAddress = "127.0.0.1"
            },
            DatabaseConstants.CurrentDbVersion,
            ct);

        if (reply.Ok && ThrowAfterNextSuccessfulEnrollmentCompletion)
        {
            ThrowAfterNextSuccessfulEnrollmentCompletion = false;
            throw new IOException("The enrollment reply was lost after the target committed the imported profile.");
        }

        return reply;
    }

    public async Task<bool> SendDeltasAsync(
        string host,
        int port,
        string serverFingerprintHex,
        IEnumerable<NetworkDelta> deltas,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(deltas);
        DeltaSendCalls++;

        var list = deltas.OrderBy(delta => delta.Ts).ToArray();
        if (list.Length == 0)
            return true;

        if (list.Length > SyncConstants.MaxIncomingDeltaCountPerCall ||
            list.Sum(delta => (long)delta.Payload.Length) > SyncConstants.MaxIncomingDeltaTotalBytesPerCall)
        {
            return false;
        }

        if (FailNextDeltaSend)
        {
            FailNextDeltaSend = false;
            return false;
        }

        try
        {
            var destinationHandler = ResolveHandler(serverFingerprintHex);
            var context = await CreateAuthenticatedSyncContextAsync(destinationHandler, ct);
            var ack = await destinationHandler.PushDeltaAsync(ToChunksAsync(list, ct), context, ct);

            if (ack.LastSyncedTs < list.Max(delta => delta.Ts))
                return false;

            foreach (var expected in list.Where(delta => delta.SnapshotOriginRevision > 0))
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

            foreach (var expected in list.Where(delta => delta.ControlOperationOriginSequence > 0))
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
        catch (Exception ex) when (ex is
            IOException or
            InvalidDataException or
            InvalidOperationException or
            CryptographicException or
            AuthenticationException or
            ArgumentException or
            OperationCanceledException or
            SyncProtocolException)
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
        SnapshotInventoryExchangeCalls++;
        var destinationHandler = ResolveHandler(serverFingerprintHex);
        var context = await CreateAuthenticatedSyncContextAsync(destinationHandler, ct);
        return await destinationHandler.ExchangeUserSnapshotInventoryAsync(request, context, ct);
    }

    public async Task<IReadOnlyList<NetworkDelta>> RequestUserSnapshotsAsync(
        string host,
        int port,
        string serverFingerprintHex,
        UserSnapshotRequestBatch request,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        SnapshotRequestCalls++;
        var destinationHandler = ResolveHandler(serverFingerprintHex);
        var context = await CreateAuthenticatedSyncContextAsync(destinationHandler, ct);
        return await destinationHandler.RequestUserSnapshotsAsync(request, context, ct);
    }

    public async Task<UserControlOperationInventoryExchangeReply> ExchangeUserControlOperationInventoryAsync(
        string host,
        int port,
        string serverFingerprintHex,
        UserControlOperationInventoryExchangeRequest request,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ControlOperationInventoryExchangeCalls++;
        var destinationHandler = ResolveHandler(serverFingerprintHex);
        var context = await CreateAuthenticatedSyncContextAsync(destinationHandler, ct);
        return await destinationHandler.ExchangeUserControlOperationInventoryAsync(request, context, ct);
    }

    public async Task<IReadOnlyList<NetworkDelta>> RequestUserControlOperationsAsync(
        string host,
        int port,
        string serverFingerprintHex,
        UserControlOperationRequestBatch request,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ControlOperationRequestCalls++;
        var destinationHandler = ResolveHandler(serverFingerprintHex);
        var context = await CreateAuthenticatedSyncContextAsync(destinationHandler, ct);
        return await destinationHandler.RequestUserControlOperationsAsync(request, context, ct);
    }

    private async Task<PeerConnectionContext> CreateAuthenticatedSyncContextAsync(
        SyncPeerProtocolHandler destinationHandler,
        CancellationToken ct)
    {
        var localIdentity = GetLocalIdentity();
        var context = new PeerConnectionContext
        {
            ClientCertificateFingerprint = ClientCertificateFingerprintOverride ?? localIdentity.FingerprintHex,
            RemoteIpAddress = "127.0.0.1"
        };

        var hello = await destinationHandler.HelloAsync(
            new HelloRequest
            {
                DeviceId = localIdentity.DeviceIdHex,
                SignPub = ByteString.CopyFrom(localIdentity.SignPublicKey),
                DatabaseVersion = DatabaseConstants.CurrentDbVersion,
                ProtocolVersion = SyncConstants.SyncProtocolVersion
            },
            context,
            ct);

        var remoteIdentity = ResolveIdentity(destinationHandler);
        if (!hello.Ok ||
            hello.ProtocolVersion != SyncConstants.SyncProtocolVersion ||
            !hello.SyncAvailable ||
            !string.Equals(hello.DeviceId, remoteIdentity.DeviceIdHex, StringComparison.OrdinalIgnoreCase) ||
            !CryptographicOperations.FixedTimeEquals(hello.SignPub.ToByteArray(), remoteIdentity.SignPublicKey))
        {
            throw new InvalidDataException("The remote peer rejected the in-process synchronization hello or returned the wrong peer identity.");
        }

        _localPresenceRegistry?.RefreshAuthenticated(
            remoteIdentity.FingerprintHex,
            new DiscoveredDeviceEndpoint
            {
                Host = "127.0.0.1",
                Port = SyncConstants.SyncPort,
                TlsCertFingerprint = remoteIdentity.FingerprintHex
            },
            DevicePresenceObservationSource.OutgoingSync);

        context.RemoteDatabaseVersion = DatabaseConstants.CurrentDbVersion;
        context.RemoteProtocolVersion = SyncConstants.SyncProtocolVersion;
        context.SyncHelloAccepted = true;
        return context;
    }

    private IDeviceIdentityService GetLocalIdentity() =>
        _localIdentity
        ?? throw new InvalidOperationException("The in-process transport has no local backend identity.");

    private IDeviceIdentityService ResolveIdentity(SyncPeerProtocolHandler handler)
    {
        if (ReferenceEquals(handler, _localHandler) && _localIdentity is not null)
            return _localIdentity;
        if (ReferenceEquals(handler, _remoteHandler) && _remoteIdentity is not null)
            return _remoteIdentity;

        throw new InvalidOperationException("The in-process transport cannot resolve the destination identity.");
    }

    private SyncPeerProtocolHandler ResolveHandler(
        string expectedFingerprint,
        bool allowEnrollmentFingerprintPrefix = false)
    {
        if (!string.IsNullOrWhiteSpace(PresentedServerCertificateFingerprintOverride) &&
            !FingerprintMatches(
                expectedFingerprint,
                PresentedServerCertificateFingerprintOverride,
                allowEnrollmentFingerprintPrefix))
        {
            throw new AuthenticationException("The presented in-process server certificate does not match the pinned fingerprint.");
        }

        if (_localHandler is not null &&
            _localIdentity is not null &&
            FingerprintMatches(
                expectedFingerprint,
                _localIdentity.FingerprintHex,
                allowEnrollmentFingerprintPrefix))
        {
            return _localHandler;
        }

        if (_remoteHandler is not null &&
            _remoteIdentity is not null &&
            FingerprintMatches(
                expectedFingerprint,
                _remoteIdentity.FingerprintHex,
                allowEnrollmentFingerprintPrefix))
        {
            return _remoteHandler;
        }

        if (_remoteHandler is null || _remoteIdentity is null)
        {
            throw new InvalidOperationException(
                "The in-process transport is not connected and the pinned fingerprint does not identify the local backend.");
        }

        throw new AuthenticationException(
            "The pinned TLS fingerprint does not match either in-process backend identity.");
    }

    private static bool FingerprintMatches(
        string expectedFingerprint,
        string actualFingerprint,
        bool allowEnrollmentFingerprintPrefix = false)
    {
        var expected = FingerprintUtil.Normalize(expectedFingerprint);
        var actual = FingerprintUtil.Normalize(actualFingerprint);

        if (expected.Length == 64)
            return string.Equals(expected, actual, StringComparison.OrdinalIgnoreCase);

        return allowEnrollmentFingerprintPrefix &&
            expected.Length == 32 &&
            actual.StartsWith(expected, StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsDurableSnapshotReceipt(UserSnapshotReceiptStateProto state) =>
        state is
            UserSnapshotReceiptStateProto.UserSnapshotReceiptStoredPending or
            UserSnapshotReceiptStateProto.UserSnapshotReceiptReplacedOlderPending or
            UserSnapshotReceiptStateProto.UserSnapshotReceiptAlreadyStored or
            UserSnapshotReceiptStateProto.UserSnapshotReceiptStoredMergedReceipt or
            UserSnapshotReceiptStateProto.UserSnapshotReceiptMergedImmediately or
            UserSnapshotReceiptStateProto.UserSnapshotReceiptObsoleteRevision or
            UserSnapshotReceiptStateProto.UserSnapshotReceiptRejectedAccountDeleted;

    private static bool IsDurableControlOperationReceipt(UserControlOperationReceiptStateProto state) =>
        state is
            UserControlOperationReceiptStateProto.UserControlOperationReceiptStoredPending or
            UserControlOperationReceiptStateProto.UserControlOperationReceiptAlreadyStored or
            UserControlOperationReceiptStateProto.UserControlOperationReceiptApplied or
            UserControlOperationReceiptStateProto.UserControlOperationReceiptObsolete;

    private static async IAsyncEnumerable<DeltaChunk> ToChunksAsync(
        IEnumerable<NetworkDelta> deltas,
        [EnumeratorCancellation] CancellationToken ct)
    {
        foreach (var delta in deltas)
        {
            ct.ThrowIfCancellationRequested();
            yield return DeltaMapping.ToProto(delta);
            await Task.Yield();
        }
    }

    private static async IAsyncEnumerable<CompleteDeviceEnrollmentChunk> TamperFirstSnapshotByteAsync(
        IAsyncEnumerable<CompleteDeviceEnrollmentChunk> chunks,
        [EnumeratorCancellation] CancellationToken ct)
    {
        var tampered = false;
        await foreach (var chunk in chunks.WithCancellation(ct))
        {
            if (!tampered && chunk.SnapshotChunk.Length > 0)
            {
                var replacement = chunk.Clone();
                var bytes = replacement.SnapshotChunk.ToByteArray();
                bytes[0] ^= 0x01;
                replacement.SnapshotChunk = ByteString.CopyFrom(bytes);
                tampered = true;
                yield return replacement;
            }
            else
            {
                yield return chunk;
            }
        }
    }
}
