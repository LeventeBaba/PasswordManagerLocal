namespace PasswordManagerLocal.Common.Backend.Models;

public enum TombstoneGarbageCollectionReason : byte
{
    Stable = 0,
    MissingCausalReference = 1,
    InvalidCausalReference = 2,
    UnknownHistoricalMembership = 3,
    MissingMergedReceipt = 4,
    StoredOnlyKnowledge = 5,
    MissingRemovalCutoff = 6,
    RemovalCutoffNotMerged = 7,
    QuarantinedEvidence = 8,
    PendingSnapshotMerge = 9,
    EvidenceLimitExceeded = 10,
    InvalidAuthenticatedEvidence = 11,
    KeyUnavailable = 12,
    AccountDeleted = 13,
    UserNotFound = 14,
    ConcurrentCanonicalChange = 15
}
