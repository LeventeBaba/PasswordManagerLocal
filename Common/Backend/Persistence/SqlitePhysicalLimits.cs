using PasswordManagerLocal.Common.Backend.Constants;

namespace PasswordManagerLocal.Common.Backend.Persistence;

/// <summary>File lengths, independent of logical row admission and checkpoint targets.</summary>
internal sealed record SqlitePhysicalLimits(
    long MainBytes = StorageCapacityConstants.MainDatabaseBytes,
    long WalBytes = StorageCapacityConstants.MaximumWalBytes,
    long JournalBytes = StorageCapacityConstants.MaximumJournalBytes,
    long TemporaryBytes = StorageCapacityConstants.MaximumTemporaryBytes,
    long SharedMemoryBytes = StorageCapacityConstants.MaximumSharedMemoryBytes)
{
    public static readonly SqlitePhysicalLimits Default = new();

    // Each 32 KiB WAL-index block covers 4096 frames, except the first (4062).
    // Use SQLite's minimum 512-byte database page and leave one frame spare. Refuse WAL
    // growth before a later index append could need a mapping beyond its allowance.
    // This matters because index append can follow the commit-frame write.
    internal long EffectiveWalBytes => Math.Min(WalBytes,
        32 + Math.Max(0, SharedMemoryBytes / 32768 * 4096 - 35) * (512 + 24));

    public void Validate()
    {
        if (MainBytes <= 0 || MainBytes > StorageCapacityConstants.MainDatabaseBytes ||
            WalBytes <= 0 || WalBytes > StorageCapacityConstants.MaximumWalBytes ||
            JournalBytes <= 0 || JournalBytes > StorageCapacityConstants.MaximumJournalBytes ||
            TemporaryBytes <= 0 || TemporaryBytes > StorageCapacityConstants.MaximumTemporaryBytes ||
            SharedMemoryBytes < 32768 || SharedMemoryBytes > StorageCapacityConstants.MaximumSharedMemoryBytes)
            throw new ArgumentOutOfRangeException(nameof(SqlitePhysicalLimits));
    }
}
