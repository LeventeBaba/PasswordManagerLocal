namespace PasswordManagerLocal.Common.Backend.Models;

public enum DeviceEnrollmentCommitStatus : byte
{
    PendingTransfer = 1,
    Transferred = 2,
    TransferFailed = 3,
    Revoked = 4
}
