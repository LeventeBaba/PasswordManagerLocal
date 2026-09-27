namespace PasswordManagerLocal.Common.Backend.Models;

/// <summary>
/// Durable control-plane sequencing and fail-closed conflict state. It intentionally survives
/// deletion of the canonical User row.
/// </summary>
public sealed class UserControlState
{
    public Guid UserId { get; set; }
    public Guid LocalOriginInstanceId { get; set; }
    public long NextOriginSequence { get; set; } = 1;
    public long AppliedKeyEpoch { get; set; }
    public long AppliedMembershipEpoch { get; set; }
    public bool HasConflict { get; set; }
    public string? ConflictReason { get; set; }
    public Guid? ConflictingOperationId { get; set; }
    public byte[]? ConflictingOperationHash { get; set; }
    public DateTimeOffset LastUpdatedAtUtc { get; set; } = DateTimeOffset.UtcNow;
}
