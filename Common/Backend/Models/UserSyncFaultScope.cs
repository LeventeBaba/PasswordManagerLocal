namespace PasswordManagerLocal.Common.Backend.Models;

public enum UserSyncFaultScope : byte
{
    LocalCanonical = 0,
    SnapshotOrigin = 1,
    SnapshotFork = 2,
    DeterministicItem = 3,
    LoginIdentity = 4,
    ControlPlane = 5,
    Database = 6
}
