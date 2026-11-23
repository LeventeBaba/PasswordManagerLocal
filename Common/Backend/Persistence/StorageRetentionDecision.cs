namespace PasswordManagerLocal.Common.Backend.Persistence;

/// <summary>Collection-level retention inventory, never a deletion permit.</summary>
internal sealed record StorageRetentionDecision(string TableName, long RowCount, long ChargedBytes,
    StorageRetentionReason Reason)
{
    public long ReclaimableBytes => 0;
}
