using PasswordManagerLocal.Common.Backend.Abstractions.Services;
using PasswordManagerLocal.Common.Backend.Models;
using PasswordManagerLocal.Common.Backend.Security;
using PasswordManagerLocal.Common.Backend.Sync;
using PasswordManagerLocal.Common.Backend.Sync.Recovery;

namespace PasswordManagerLocal.Common.Tests.Fakes;

public sealed class FakeUserDataBundleSyncService : IUserDataBundleSyncService
{
    public Func<User, IReadOnlyList<UserSnapshotEnvelope>, EncryptionKey, CancellationToken, Task<UserSnapshotMergeBatchResult>>? Handler { get; set; }
    public Func<User, IReadOnlyList<UserSnapshotEnvelope>, EncryptionKey, UserSyncKeyConfidence, long, long, CancellationToken, Task<UserDataRecoveryReconstructionResult>>? RecoveryHandler { get; set; }

    public Task<UserSnapshotMergeBatchResult> TryVerifyAndMergeManyAsync(
        User existing,
        IReadOnlyList<UserSnapshotEnvelope> snapshots,
        EncryptionKey key,
        CancellationToken ct = default) =>
        Handler?.Invoke(existing, snapshots, key, ct)
        ?? Task.FromResult(new UserSnapshotMergeBatchResult(
            CanonicalChanged: false,
            snapshots.Select(snapshot => new UserSnapshotMergeEntryResult(
                snapshot.OriginDeviceId,
                snapshot.OriginInstanceId,
                snapshot.OriginRevision,
                Verified: true)).ToArray()));

    public async Task<PreparedCanonicalRecovery> PrepareCanonicalRecoveryAsync(
        User source, UserCanonicalCheckpoint? checkpoint, IReadOnlyList<UserSnapshotEnvelope> snapshots,
        EncryptionKey key, UserSyncKeyConfidence keyConfidence, long expectedKeyEpoch, long expectedMembershipEpoch,
        CancellationToken ct = default)
    {
        var working = PreparedCanonicalRecovery.Copy(source);
        var result = RecoveryHandler is null ? new UserDataRecoveryReconstructionResult(false, [], "fake-recovery-not-configured")
            : await RecoveryHandler(working, snapshots, key, keyConfidence, expectedKeyEpoch, expectedMembershipEpoch, ct);
        return new PreparedCanonicalRecovery(working, result, source);
    }
}
