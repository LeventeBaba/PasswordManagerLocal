using PasswordManagerLocal.Common.Backend.Sync;

namespace PasswordManagerLocal.Common.Backend.Abstractions.Services;

public interface IUserSnapshotAntiEntropyService
{
    Task<UserSnapshotInventoryExchangeRequest> BuildInventoryAsync(Guid peerDeviceId, CancellationToken ct = default);

    Task<UserSnapshotInventoryExchangeReply> BuildInventoryReplyAsync(
        Guid peerDeviceId,
        UserSnapshotInventoryExchangeRequest remoteInventory,
        CancellationToken ct = default);

    IReadOnlyList<UserSnapshotRequest> FindMissingSnapshots(
        UserSnapshotInventoryExchangeRequest localInventory,
        IEnumerable<UserSnapshotUserInventory> remoteInventory);

    Task<IReadOnlyList<NetworkDelta>> BuildRequestedSnapshotDeltasAsync(
        Guid peerDeviceId,
        IEnumerable<UserSnapshotRequest> requests,
        CancellationToken ct = default);
}
