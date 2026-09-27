namespace PasswordManagerLocal.Common.Backend.Constants;

public static class TombstoneConstants
{
    // Generic model tombstones remain fail-closed bounded by their repository. Encrypted user-data
    // tombstones are never discarded by age or list position; their larger caps reject writes and
    // evidence processing explicitly while synchronization remains available to acquire coverage.
    public const int MaxSyncTombstones = 4096;
    public const int MaxRetainedUserDataTombstonesPerList = 65_536;
    public const int MaxRetainedUserDataTombstonesPerUser = MaxRetainedUserDataTombstonesPerList * 4;
    public const int MaxMembershipHistoryRowsPerUser = 4096;
    public const int MaxRemovalCutoffRowsPerUser = 16_384;
    public const int MaxRevisionKnowledgeRowsPerUser = 16_384;
    public const int MaxCausalSnapshotEvidenceRowsPerUser = 16_384;
    public const int MaxTombstoneGarbageCollectionDiagnostics = 256;
}
