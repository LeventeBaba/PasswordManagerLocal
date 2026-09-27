namespace PasswordManagerLocal.Common.Backend.Sync;

public enum UserControlOperationReceiptState : byte
{
    StoredPending = 1,
    AlreadyStored = 2,
    Applied = 3,
    Obsolete = 4,
    Quarantined = 5,
    Rejected = 6
}
