namespace PasswordManagerLocal.Common.Backend.Persistence;

/// <summary>A read-only finding; the compactor must revalidate the current record before writing.</summary>
internal sealed record StorageEvidenceRetentionDecision(
    string TableName, Guid RecordId, Guid UserId, StorageRetentionReason Reason,
    long ReclaimableBytes = 0)
{
    public string ImmutableIdentity { get; init; } = RecordId.ToString("D");
    // Stored references are retention constraints, not newly manufactured authority.
    public IReadOnlyList<string> Dependencies { get; init; } = [];
}
