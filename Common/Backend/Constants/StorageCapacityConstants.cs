namespace PasswordManagerLocal.Common.Backend.Constants;

/// <summary>Finite admission budgets, in charged bytes; these are not physical file sizes.</summary>
public static class StorageCapacityConstants
{
    public const long MiB = 1024L * 1024;
    public const long GiB = 1024 * MiB;
    public const long ContentBytes = GiB;
    public const long SnapshotBytes = GiB;
    public const long ControlBytes = 256 * MiB;
    public const long RevocationBytes = 512 * MiB;
    public const long BarrierBytes = 64 * MiB;
    public const long RecoveryBytes = 512 * MiB;
    public const long IdentityBytes = 32 * MiB;
    public const long WorkBytes = 128 * MiB;
    public const long CriticalIngressBytes = 256 * MiB;
    public const int MaxCriticalIngressRows = 1024;
    public const long MainDatabaseBytes = 8 * GiB;
    public const long MaximumWalBytes = 2 * GiB;
    public const long MaximumJournalBytes = 16 * GiB;
    public const long MaximumTemporaryBytes = 2 * GiB;
    public const long MaximumSharedMemoryBytes = 64 * MiB;
    public const int MaximumTemporaryFiles = 64;
    public const int IncrementalVacuumPages = 256;
    public const long MigrationWorkspaceReserveBytes = 64 * MiB;
    // Truncation target after a WAL reset, not a ceiling while readers pin the WAL.
    public const long RetainedJournalBytes = 64 * MiB;
    public const int WalAutoCheckpointPages = 1024;
    // Admission threshold, not a hard byte ceiling: an admitted transaction can overshoot at commit.
    public const long WalPressureBytes = GiB;
    public static readonly TimeSpan WalPressureRetryInterval = TimeSpan.FromSeconds(1);
    public const int MaxProfiles = 256;
    public const int RowChargeBytes = 128;
    // Includes the existing 128 MiB recovery payload limit plus encryption/row overhead.
    public const int MaxLargeRowBytes = 129 * 1024 * 1024;
    public const int MaxDecodedUserDataBytes = 128 * 1024 * 1024;
    public const int MaxMetadataRowBytes = 64 * 1024;
    public const int MaxDatabaseConfigurationBytes = 64 * 1024;
}
