using PasswordManagerLocal.Common.Backend.Models;
using PasswordManagerLocal.Common.Backend.Security;
using PasswordManagerLocal.Common.Backend.Sync;

namespace PasswordManagerLocal.Common.Backend.Abstractions.Services;

public interface IUserDataBundleVerificationService
{
    Task<UserDataBundleVerificationResult> VerifyCanonicalAsync(
        User user,
        EncryptionKey key,
        UserSyncKeyConfidence keyConfidence,
        CancellationToken ct = default);

    Task<UserDataBundleVerificationResult> VerifySnapshotAsync(
        UserSnapshotEnvelope snapshot,
        EncryptionKey key,
        UserSyncKeyConfidence keyConfidence,
        CancellationToken ct = default);
}
