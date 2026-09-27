namespace PasswordManagerLocal.Common.Backend.Models;

public enum UserSyncFaultKind : byte
{
    CanonicalIntegrityMismatch = 0,
    CanonicalCheckpointMissing = 1,
    CanonicalCheckpointMismatch = 2,
    CanonicalCheckpointSignatureFailure = 3,
    CanonicalRootDecryptFailure = 4,
    CanonicalRootIntegrityFailure = 5,
    CanonicalGeneralBlobFailure = 6,
    CanonicalPasswordsBlobFailure = 7,
    CanonicalDevicesBlobFailure = 8,
    CanonicalBundleLinkFailure = 9,
    IncomingDecryptFailure = 10,
    IncomingIntegrityFailure = 11,
    IncomingMetadataMismatch = 12,
    UnauthorizedOrigin = 13,
    InvalidEpoch = 14,
    SameRevisionFork = 15,
    RevisionRollback = 16,
    DuplicateOriginInstallation = 17,
    DeterministicItemConflict = 18,
    LoginProjectionConflict = 19,
    KeyEpochConflict = 20,
    MembershipConflict = 21,
    ControlOperationFork = 22,
    DatabaseIntegrityFailure = 23,
    CanonicalKeyVerificationPending = 24
}
