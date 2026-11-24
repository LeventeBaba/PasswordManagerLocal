using PasswordManagerLocal.Common.Backend.Abstractions.Services;
using PasswordManagerLocal.Common.Backend.Models;
using PasswordManagerLocal.Common.Backend.Security;
using PasswordManagerLocal.Common.Backend.Sync;
using PasswordManagerLocal.Common.Backend.Sync.Recovery;

namespace PasswordManagerLocal.Common.Tests.TestInfrastructure.Services.TestDoubles;

internal sealed class ControllableRecoveryPreparationService(IUserSnapshotBatchVerificationService verification)
    : IUserDataBundleSyncService, IUserSnapshotBatchVerificationService
{
    public IUserDataBundleSyncService Inner { get; set; } = null!;
    public Func<CancellationToken, Task>? BeforeVerificationAsync { get; set; }
    public Func<CancellationToken, Task>? AfterVerificationAsync { get; set; }
    public Func<CancellationToken, Task>? AfterPreparationAsync { get; set; }
    public PreparedCanonicalRecovery? LastPrepared { get; private set; }

    public async Task<IReadOnlyList<UserDataBundleVerificationResult>> VerifyAsync(
        IReadOnlyList<UserSnapshotEnvelope> snapshots, EncryptionKey key, UserSyncKeyConfidence confidence,
        CancellationToken ct = default)
    {
        if (BeforeVerificationAsync is { } before) await before(ct);
        var results = await verification.VerifyAsync(snapshots, key, confidence, ct);
        try
        {
            if (AfterVerificationAsync is { } after) await after(ct);
            return results;
        }
        catch
        {
            foreach (var result in results) result.Dispose();
            throw;
        }
    }

    public async Task<PreparedCanonicalRecovery> PrepareCanonicalRecoveryAsync(User source,
        UserCanonicalCheckpoint? checkpoint, IReadOnlyList<UserSnapshotEnvelope> snapshots, EncryptionKey key,
        UserSyncKeyConfidence confidence, long keyEpoch, long membershipEpoch, CancellationToken ct = default)
    {
        var prepared = await Inner.PrepareCanonicalRecoveryAsync(source, checkpoint, snapshots, key,
            confidence, keyEpoch, membershipEpoch, ct);
        LastPrepared = prepared;
        try
        {
            if (AfterPreparationAsync is { } after) await after(ct);
            return prepared;
        }
        catch { prepared.Dispose(); throw; }
    }

    public Task<UserSnapshotMergeBatchResult> TryVerifyAndMergeManyAsync(User existing,
        IReadOnlyList<UserSnapshotEnvelope> snapshots, EncryptionKey key, CancellationToken ct = default) =>
        Inner.TryVerifyAndMergeManyAsync(existing, snapshots, key, ct);

    public void ObserveCommittedRecovery(PreparedCanonicalRecovery prepared) => Inner.ObserveCommittedRecovery(prepared);
}
