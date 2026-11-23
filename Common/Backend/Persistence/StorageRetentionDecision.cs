namespace PasswordManagerLocal.Common.Backend.Persistence;

/// <summary>Collection-level retention inventory, never a deletion permit.</summary>
internal sealed record StorageRetentionDecision(string TableName, long RowCount, long ChargedBytes,
    StorageRetentionReason Reason)
{
    // A lower-bound estimate from the bounded supported-evidence batch, not a deletion permit.
    public long ReclaimableBytes { get; init; }
}
