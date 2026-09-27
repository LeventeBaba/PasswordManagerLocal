namespace PasswordManagerLocal.Common.Backend.Models;

public enum UserDataRecoveryState : byte
{
    Recovered = 0,
    NothingToRecover = 1,
    AwaitingEvidence = 2,
    AwaitingPeerEvidence = 3,
    AwaitingKey = 4,
    KeyNotTrusted = 5,
    AccountDeleted = 6,
    WrongKeyEpoch = 7,
    ControlPlaneConflict = 8,
    TerminalFork = 9,
    NoHealthyCandidate = 10,
    BackoffActive = 11,
    DatabaseUnhealthy = 12,
    TransactionConflict = 13,
    Failed = 14
}
