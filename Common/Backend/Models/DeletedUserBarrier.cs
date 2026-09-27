namespace PasswordManagerLocal.Common.Backend.Models;

/// <summary>
/// Permanent authoritative deletion evidence for one user identity. This row deliberately has no
/// foreign key to User and must survive removal of every canonical and pending account row.
/// </summary>
public sealed class DeletedUserBarrier
{
    public Guid UserId { get; set; }
    public Guid DeletionOperationId { get; set; }
    public Guid DeletionGeneration { get; set; }
    public Guid OriginDeviceId { get; set; }
    public Guid OriginInstanceId { get; set; }
    public long OriginSequence { get; set; }
    public long KeyEpoch { get; set; }
    public long MembershipEpoch { get; set; }
    public DateTimeOffset DeletedAtUtc { get; set; }
    public DateTimeOffset AppliedAtUtc { get; set; }
    public DateTimeOffset LastUpdatedAtUtc { get; set; }
    public byte[] OperationHash { get; set; } = [];
    public byte[] OriginSignPublicKey { get; set; } = [];
    public byte[] OriginSignature { get; set; } = [];
    public bool HasConflict { get; set; }
    public Guid? ConflictingOperationId { get; set; }
    public byte[]? ConflictingOperationHash { get; set; }
}
