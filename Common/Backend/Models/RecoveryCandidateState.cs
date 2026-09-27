namespace PasswordManagerLocal.Common.Backend.Models;

public enum RecoveryCandidateState : byte
{
    Healthy = 0,
    InvalidEnvelope = 1,
    InvalidSignature = 2,
    UnauthorizedOrigin = 3,
    WrongKeyEpoch = 4,
    PostRemovalCutoff = 5,
    DecryptFailed = 6,
    IntegrityFailed = 7,
    MetadataMismatch = 8,
    Forked = 9,
    Superseded = 10,
    ControlPlaneConflict = 11
}
