namespace PasswordManagerLocal.Common.Backend.Persistence;

internal enum StorageRetentionReason
{
    SafeToCompact,
    AlreadyCompact,
    RequiredAsExactSignedReceipt,
    RequiredForForkDetection,
    BlockedByPendingEvidence,
    BlockedByConflict,
    RequiredBySnapshot,
    RequiredByAuthorization,
    RequiredByRevocation,
    RequiredByRecovery,
    RequiredByCausalCoverage,
    InsufficientProof
}
