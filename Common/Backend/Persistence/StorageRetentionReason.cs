namespace PasswordManagerLocal.Common.Backend.Persistence;

internal enum StorageRetentionReason
{
    RequiredBySnapshot,
    RequiredByAuthorization,
    RequiredByRevocation,
    RequiredByRecovery,
    RequiredByCausalCoverage,
    InsufficientProof
}
