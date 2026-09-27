using PasswordManagerLocal.Common.Backend.Models;
using PasswordManagerLocal.Common.Backend.Security;
using PasswordManagerLocal.Common.Backend.Sync;

namespace PasswordManagerLocal.Common.Backend.Abstractions.Services;

public interface IUserSnapshotBatchVerificationService
{
    Task<IReadOnlyList<UserDataBundleVerificationResult>> VerifyAsync(
        IReadOnlyList<UserSnapshotEnvelope> snapshots,
        EncryptionKey key,
        UserSyncKeyConfidence keyConfidence,
        CancellationToken ct = default);
}
