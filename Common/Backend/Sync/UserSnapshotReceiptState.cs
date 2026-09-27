namespace PasswordManagerLocal.Common.Backend.Sync;

public enum UserSnapshotReceiptState
{
    None = 0,
    StoredPending = 1,
    ReplacedOlderPending = 2,
    AlreadyStored = 3,
    MergedImmediately = 4,
    ObsoleteRevision = 5,
    NeedsKey = 6,
    WrongKeyEpoch = 7,
    WrongMembershipEpoch = 8,
    Quarantined = 9,
    Rejected = 10,
    RejectedAccountDeleted = 11,
    StoredMergedReceipt = 12
}
