using PasswordManagerLocal.Common.Backend.Sync;

namespace PasswordManagerLocal.Common.Backend.Abstractions.Services;

public interface IUserSnapshotInboxService
{
    Task<UserSnapshotReceiptResult> StoreAsync(UserSnapshotEnvelope envelope, Guid transportPeerDeviceId, CancellationToken ct = default);
}
