namespace PasswordManagerLocal.Common.Backend.Sync.Enrollment;

public sealed class DeviceEnrollmentControlStateSnapshot
{
    public Guid UserId { get; set; }
    public long AppliedKeyEpoch { get; set; }
    public long AppliedMembershipEpoch { get; set; }
    public bool HasConflict { get; set; }
    public string? ConflictReason { get; set; }
    public Guid? ConflictingOperationId { get; set; }
    public byte[]? ConflictingOperationHash { get; set; }
}
