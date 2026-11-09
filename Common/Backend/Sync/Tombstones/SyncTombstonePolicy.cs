namespace PasswordManagerLocal.Common.Backend.Sync.Tombstones;

internal static class SyncTombstonePolicy
{
    public static void EnsureCapacity(int capacity, int persisted, int pending)
    {
        if (capacity < 1 || persisted + (long)pending >= capacity)
            throw CapacityExceeded(capacity);
    }

    public static InvalidOperationException CapacityExceeded(int capacity) => capacity < 1
        ? new("Synchronization tombstone storage is disabled; deletion knowledge cannot be safely recorded.")
        : new("The synchronization tombstone safety limit was reached. Tombstones were preserved to prevent data resurrection; manual maintenance or causal garbage collection is required.");
}
