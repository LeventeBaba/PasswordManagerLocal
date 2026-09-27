using PasswordManagerLocal.Common.Backend.Sync;

namespace PasswordManagerLocal.Common.Backend.Abstractions.Services;

public interface INetworkDeltaReplayService
{
    Task<bool> IsBlockedByNewerTombstoneAsync(SyncDeltaPayload payload, long ts, CancellationToken ct = default);
    Task<bool> IsAlreadyAppliedAsync(SyncDeltaPayload payload, long ts, CancellationToken ct = default);
}
