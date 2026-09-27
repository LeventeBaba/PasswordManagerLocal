using PasswordManagerLocal.Common.Backend.Models;
using PasswordManagerLocal.Common.Backend.Sync;

namespace PasswordManagerLocal.Common.Backend.Abstractions.Services;

public interface IOutgoingDeltaBuilderService
{
    Task<NetworkDelta> BuildAsync(SyncItem item, Device device, CancellationToken ct = default);
    Task<NetworkDelta> BuildUserSnapshotRelayAsync(UserSyncSnapshot snapshot, Device device, CancellationToken ct = default);
    Task<NetworkDelta> BuildUserControlOperationRelayAsync(UserControlOperation operation, Device device, CancellationToken ct = default);
}
