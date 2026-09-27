namespace PasswordManagerLocal.Common.Backend.Models;

public sealed record TombstoneGarbageCollectionResult(
    Guid UserId,
    bool Changed,
    int ExaminedCount,
    int RemovedCount,
    int RetainedCount,
    TombstoneGarbageCollectionReason? GlobalBlockReason,
    IReadOnlyList<TombstoneGarbageCollectionDiagnostic> Diagnostics)
{
    public static TombstoneGarbageCollectionResult Blocked(
        Guid userId,
        TombstoneGarbageCollectionReason reason,
        int retainedCount = 0,
        IReadOnlyList<TombstoneGarbageCollectionDiagnostic>? diagnostics = null) =>
        new(userId, false, retainedCount, 0, retainedCount, reason, diagnostics ?? []);
}
