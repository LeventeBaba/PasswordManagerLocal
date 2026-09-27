namespace PasswordManagerLocal.Common.Backend.Models;

/// <summary>
/// Recoverable record for an authoritative addition committed before bootstrap transfer.
/// </summary>
public sealed class DeviceEnrollmentCommit
{
    public Guid CommitId { get; set; } = Guid.NewGuid();
    public Guid UserId { get; set; }
    public Guid TargetDeviceId { get; set; }
    public Guid TargetOriginInstanceId { get; set; }
    public byte[] TargetSignPublicKeyHash { get; set; } = [];
    public byte[] TargetAgreementPublicKeyHash { get; set; } = [];
    public string TargetTlsCertFingerprint { get; set; } = string.Empty;
    public DeviceType TargetDeviceType { get; set; }
    public Guid AdditionOperationId { get; set; }
    public byte[] AdditionOperationHash { get; set; } = [];
    public DeviceEnrollmentCommitStatus Status { get; set; } = DeviceEnrollmentCommitStatus.PendingTransfer;
    public string? LastError { get; set; }
    public DateTimeOffset CreatedAtUtc { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset LastAttemptAtUtc { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? CompletedAtUtc { get; set; }
    public long Version { get; set; }
}
