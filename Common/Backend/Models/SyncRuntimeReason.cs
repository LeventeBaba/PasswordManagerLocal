namespace PasswordManagerLocal.Common.Backend.Models;

[Flags]
public enum SyncRuntimeReason
{
    None = 0,
    NormalProfileSynchronization = 1,
    PendingRevocationDelivery = 2,
    ActiveEnrollment = 4,
    ActiveInFlightSynchronization = 8
}
