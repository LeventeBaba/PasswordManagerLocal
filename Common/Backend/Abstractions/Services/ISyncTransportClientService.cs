using PasswordManagerLocal.Common.Backend.Sync;
using PasswordManagerLocal.Common.Backend.Sync.Presence;

namespace PasswordManagerLocal.Common.Backend.Abstractions.Services;

public interface ISyncTransportClientService
{
    Task<DevicePresenceProbeResult> ProbeAsync(
        string host,
        int port,
        string serverFingerprintHex,
        string expectedDeviceId,
        byte[] expectedSignPublicKey,
        CancellationToken ct = default);

    Task<bool> SendDeltasAsync(string host, int port, string serverFingerprintHex, IEnumerable<NetworkDelta> deltas, CancellationToken ct = default);

    Task<UserSnapshotInventoryExchangeReply> ExchangeUserSnapshotInventoryAsync(
        string host,
        int port,
        string serverFingerprintHex,
        UserSnapshotInventoryExchangeRequest request,
        CancellationToken ct = default);

    Task<IReadOnlyList<NetworkDelta>> RequestUserSnapshotsAsync(
        string host,
        int port,
        string serverFingerprintHex,
        UserSnapshotRequestBatch request,
        CancellationToken ct = default);

    Task<UserControlOperationInventoryExchangeReply> ExchangeUserControlOperationInventoryAsync(
        string host,
        int port,
        string serverFingerprintHex,
        UserControlOperationInventoryExchangeRequest request,
        CancellationToken ct = default);

    Task<IReadOnlyList<NetworkDelta>> RequestUserControlOperationsAsync(
        string host,
        int port,
        string serverFingerprintHex,
        UserControlOperationRequestBatch request,
        CancellationToken ct = default);

    Task<GetDeviceEnrollmentInfoReply> GetDeviceEnrollmentInfoAsync(string host, int port, string serverFingerprintHex, GetDeviceEnrollmentInfoRequest request, CancellationToken ct = default);

    Task<CompleteDeviceEnrollmentReply> CompleteDeviceEnrollmentStreamAsync(string host, int port, string serverFingerprintHex, IAsyncEnumerable<CompleteDeviceEnrollmentChunk> chunks, CancellationToken ct = default);
}
