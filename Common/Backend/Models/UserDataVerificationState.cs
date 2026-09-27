namespace PasswordManagerLocal.Common.Backend.Models;

public enum UserDataVerificationState : byte
{
    Healthy = 0,
    RowIntegrityFailure = 1,
    CheckpointMissing = 2,
    CheckpointFailure = 3,
    RootDecryptFailure = 4,
    RootIntegrityFailure = 5,
    GeneralBlobFailure = 6,
    PasswordsBlobFailure = 7,
    DevicesBlobFailure = 8,
    BundleLinkFailure = 9,
    LoginMetadataFailure = 10,
    KeyNotConfirmed = 11,
    DeletedAccount = 12,
    AuthorizationFailure = 13,
    EpochFailure = 14,
    Fork = 15
}
