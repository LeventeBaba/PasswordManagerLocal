namespace PasswordManagerLocal.Common.Backend.Models;

/// <summary>
/// Immutable, origin-signed control-plane operation. This row deliberately has no cascading
/// foreign key to User so authoritative barriers and diagnostics can outlive canonical data.
/// </summary>
public sealed class UserControlOperation
{
    public Guid OperationId { get; set; }
    public Guid UserId { get; set; }
    public UserControlOperationType OperationType { get; set; }
    public Guid OriginDeviceId { get; set; }
    public Guid OriginInstanceId { get; set; }
    public long OriginSequence { get; set; }
    public long PreviousKeyEpoch { get; set; }
    public long ResultingKeyEpoch { get; set; }
    public long PreviousMembershipEpoch { get; set; }
    public long ResultingMembershipEpoch { get; set; }
    public DateTimeOffset CreatedAtUtc { get; set; }
    public DateTimeOffset ReceivedAtUtc { get; set; }
    public DateTimeOffset? AppliedAtUtc { get; set; }
    public Guid? LastReceivedFromDeviceId { get; set; }
    public byte[] PayloadHash { get; set; } = [];
    public byte[] OperationHash { get; set; } = [];
    public byte[] OriginSignPublicKey { get; set; } = [];
    public byte[] OriginSignature { get; set; } = [];
    public byte[] EnvelopePayload { get; set; } = [];
    public UserControlOperationStatus Status { get; set; } = UserControlOperationStatus.StoredPending;
    public string? StatusReason { get; set; }
    public byte[]? ConflictingOperationHash { get; set; }
}
