using PasswordManagerLocal.Common.Backend.Models;

namespace PasswordManagerLocal.Common.Backend.Sync;

/// <summary>Recovery eligibility derived from the existing fault model, never new authority.</summary>
public static class UserSyncFaultPolicy
{
    public static bool IsTerminal(UserSyncFault fault) => IsTerminal(fault.Scope, fault.Kind, fault.Status);

    public static bool IsTerminal(UserSyncFaultScope scope, UserSyncFaultKind kind, UserSyncHealthStatus status) =>
        status == UserSyncHealthStatus.TerminalConflict ||
        scope is UserSyncFaultScope.SnapshotFork or UserSyncFaultScope.ControlPlane or UserSyncFaultScope.DeterministicItem ||
        kind is UserSyncFaultKind.SameRevisionFork or UserSyncFaultKind.RevisionRollback or
            UserSyncFaultKind.DuplicateOriginInstallation or UserSyncFaultKind.DeterministicItemConflict or
            UserSyncFaultKind.LoginProjectionConflict or UserSyncFaultKind.KeyEpochConflict or
            UserSyncFaultKind.MembershipConflict or UserSyncFaultKind.ControlOperationFork;

    public static bool IsRecoverableCanonical(UserSyncFault fault) =>
        fault.Scope == UserSyncFaultScope.LocalCanonical && !IsTerminal(fault) &&
        fault.Kind is UserSyncFaultKind.CanonicalIntegrityMismatch or UserSyncFaultKind.CanonicalCheckpointMissing or
            UserSyncFaultKind.CanonicalCheckpointMismatch or UserSyncFaultKind.CanonicalCheckpointSignatureFailure or
            UserSyncFaultKind.CanonicalRootDecryptFailure or UserSyncFaultKind.CanonicalRootIntegrityFailure or
            UserSyncFaultKind.CanonicalGeneralBlobFailure or UserSyncFaultKind.CanonicalPasswordsBlobFailure or
            UserSyncFaultKind.CanonicalDevicesBlobFailure or UserSyncFaultKind.CanonicalBundleLinkFailure or
            UserSyncFaultKind.CanonicalKeyVerificationPending;

    public static bool IsRecoverableOrigin(UserSyncFault fault) =>
        fault.Scope == UserSyncFaultScope.SnapshotOrigin && !IsTerminal(fault) &&
        fault.Kind is UserSyncFaultKind.IncomingDecryptFailure or UserSyncFaultKind.IncomingIntegrityFailure or
            UserSyncFaultKind.IncomingMetadataMismatch;
}
