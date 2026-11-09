namespace PasswordManagerLocal.Common.Backend.Models;

public enum UserRecoveryJournalState : byte
{
    PendingVerification = 0,
    UnresolvedEvidence = 1
}
