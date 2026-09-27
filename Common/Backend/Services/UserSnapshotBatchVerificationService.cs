using PasswordManagerLocal.Common.Backend.Abstractions.Services;
using PasswordManagerLocal.Common.Backend.Constants;
using PasswordManagerLocal.Common.Backend.Models;
using PasswordManagerLocal.Common.Backend.Security;
using PasswordManagerLocal.Common.Backend.Sync;

namespace PasswordManagerLocal.Common.Backend.Services;

public sealed class UserSnapshotBatchVerificationService : IUserSnapshotBatchVerificationService
{
    private readonly IUserDataBundleVerificationService _verification;

    public UserSnapshotBatchVerificationService(IUserDataBundleVerificationService verification)
    {
        ArgumentNullException.ThrowIfNull(verification);
        _verification = verification;
    }

    public async Task<IReadOnlyList<UserDataBundleVerificationResult>> VerifyAsync(
        IReadOnlyList<UserSnapshotEnvelope> snapshots,
        EncryptionKey key,
        UserSyncKeyConfidence keyConfidence,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(snapshots);
        ArgumentNullException.ThrowIfNull(key);

        if (snapshots.Count == 0)
            return [];

        using var concurrencyGate = new SemaphoreSlim(
            SyncConstants.MaxConcurrentSnapshotVerifications,
            SyncConstants.MaxConcurrentSnapshotVerifications);
        var verificationTasks = new Task<UserDataBundleVerificationResult>[snapshots.Count];

        for (var index = 0; index < snapshots.Count; index++)
        {
            verificationTasks[index] = VerifyOneAsync(
                snapshots[index],
                key,
                keyConfidence,
                concurrencyGate,
                ct);
        }

        try
        {
            return await Task.WhenAll(verificationTasks);
        }
        catch
        {
            foreach (var task in verificationTasks)
            {
                if (task.Status == TaskStatus.RanToCompletion)
                    task.Result.Dispose();
            }

            throw;
        }
    }

    private async Task<UserDataBundleVerificationResult> VerifyOneAsync(
        UserSnapshotEnvelope snapshot,
        EncryptionKey key,
        UserSyncKeyConfidence keyConfidence,
        SemaphoreSlim concurrencyGate,
        CancellationToken ct)
    {
        await concurrencyGate.WaitAsync(ct);
        try
        {
            return await _verification.VerifySnapshotAsync(snapshot, key, keyConfidence, ct);
        }
        finally
        {
            concurrencyGate.Release();
        }
    }
}
