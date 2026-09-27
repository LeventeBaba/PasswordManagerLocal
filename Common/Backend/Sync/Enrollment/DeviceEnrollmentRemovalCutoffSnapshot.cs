namespace PasswordManagerLocal.Common.Backend.Sync.Enrollment;

public sealed class DeviceEnrollmentRemovalCutoffSnapshot
{
    public Guid CutoffId { get; set; }
    public Guid UserId { get; set; }
    public Guid DeviceId { get; set; }
    public Guid OriginInstanceId { get; set; }
    public long UserKeyEpoch { get; set; }
    public long HighestAcceptedSnapshotRevision { get; set; }
    public long HighestAcceptedControlSequence { get; set; }
    public long ResultingMembershipEpoch { get; set; }
    public Guid AuthorizationId { get; set; }
    public Guid RemovalOperationId { get; set; }
    public byte[] RemovalOperationHash { get; set; } = [];
    public DateTimeOffset CreatedAtUtc { get; set; }
}
