using PasswordManagerLocal.Common.Backend.Models;

namespace PasswordManagerLocal.Common.Backend.Abstractions.Services;

public interface IRevocationDeliveryService
{
    Task<IReadOnlyList<RevocationDeliverySnapshot>> GetSnapshotsAsync(CancellationToken ct = default);
    Task<bool> HasPendingDeliveryAsync(CancellationToken ct = default);
    Task<IReadOnlyList<Guid>> ListPendingUserIdsAsync(CancellationToken ct = default);
    Task<bool> HasPendingForPeerAsync(Guid deviceId, CancellationToken ct = default);
    // Called under the history-reset transaction before replacing live authority rows.
    Task StageHistoryResetAsync(Guid userId, CancellationToken ct = default);
}
