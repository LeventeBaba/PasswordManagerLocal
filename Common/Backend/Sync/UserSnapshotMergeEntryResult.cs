using PasswordManagerLocal.Common.Backend.Models;
using PasswordManagerLocal.Common.Backend.Models.Encrypted;

namespace PasswordManagerLocal.Common.Backend.Sync;

public sealed record UserSnapshotMergeEntryResult(
    Guid OriginDeviceId,
    Guid OriginInstanceId,
    long OriginRevision,
    bool Verified,
    string? FailureReason = null,
    UserDataVerificationState VerificationState = UserDataVerificationState.Healthy,
    UserDataBlobKind FailedBlobs = UserDataBlobKind.None,
    string? DiagnosticCode = null);
