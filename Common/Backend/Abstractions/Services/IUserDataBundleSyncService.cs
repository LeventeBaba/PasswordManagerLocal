using PasswordManagerLocal.Common.Backend.Models;
using PasswordManagerLocal.Common.Backend.Sync;
using PasswordManagerLocal.Common.Backend.Security;
using PasswordManagerLocal.Common.Backend.Sync.Recovery;

namespace PasswordManagerLocal.Common.Backend.Abstractions.Services;

public interface IUserDataBundleSyncService
{
    Task<UserSnapshotMergeBatchResult> TryVerifyAndMergeManyAsync(
        User existing,
        IReadOnlyList<UserSnapshotEnvelope> snapshots,
        EncryptionKey key,
        CancellationToken ct = default);

    Task<UserSnapshotMergeBatchResult> TryVerifyAndMergeManyAsync(
        User existing,
        IReadOnlyList<UserSnapshotEnvelope> snapshots,
        EncryptionKey key,
        UserSyncKeyConfidence keyConfidence,
        CancellationToken ct = default) =>
        TryVerifyAndMergeManyAsync(existing, snapshots, key, ct);
    Task<PreparedCanonicalRecovery> PrepareCanonicalRecoveryAsync(
        User source, UserCanonicalCheckpoint? checkpoint,
        IReadOnlyList<UserSnapshotEnvelope> snapshots, EncryptionKey key,
        UserSyncKeyConfidence keyConfidence, long expectedKeyEpoch, long expectedMembershipEpoch,
        CancellationToken ct = default) =>
        throw new NotSupportedException("Canonical recovery preparation is unavailable.");

    void ObserveCommittedRecovery(PreparedCanonicalRecovery prepared) { }
}
