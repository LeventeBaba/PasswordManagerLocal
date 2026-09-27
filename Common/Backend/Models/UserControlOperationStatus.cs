namespace PasswordManagerLocal.Common.Backend.Models;

public enum UserControlOperationStatus : byte
{
    StoredPending = 1,
    Applied = 2,
    Rejected = 3,
    Quarantined = 4
}
