using PasswordManagerLocal.Common.Backend.Models;
using PasswordManagerLocal.Common.Backend.Models.Encrypted;

namespace PasswordManagerLocal.Common.Backend.Sync;


public sealed record UserSnapshotMergeBatchResult(
    bool CanonicalChanged,
    IReadOnlyList<UserSnapshotMergeEntryResult> Entries,
    UserDataVerificationState CanonicalState = UserDataVerificationState.Healthy,
    UserDataBlobKind CanonicalFailedBlobs = UserDataBlobKind.None,
    string? CanonicalDiagnosticCode = null)
{
    public bool CanonicalVerified => CanonicalState == UserDataVerificationState.Healthy;
}
