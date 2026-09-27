namespace PasswordManagerLocal.Common.Backend.Models;

/// <summary>
/// Durable, non-secret evidence describing why a synchronization source or local canonical state
/// is not currently trusted. Snapshot payloads are deliberately kept outside this record.
/// </summary>
public sealed class UserSyncFault
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid UserId { get; set; }
    public UserSyncFaultScope Scope { get; set; }
    public UserSyncFaultKind Kind { get; set; }
    public UserSyncHealthStatus Status { get; set; } = UserSyncHealthStatus.Suspect;
    public string AffectedComponent { get; set; } = string.Empty;
    public Guid? OriginDeviceId { get; set; }
    public Guid? OriginInstanceId { get; set; }
    public long? KeyEpoch { get; set; }
    public long? MembershipEpoch { get; set; }
    public long? OriginRevision { get; set; }
    public byte[] ExpectedHash { get; set; } = [];
    public byte[] ObservedHash { get; set; } = [];
    public byte[] ConflictingHash { get; set; } = [];
    public DateTimeOffset FirstDetectedAtUtc { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset LastDetectedAtUtc { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? LastRecoveryAttemptAtUtc { get; set; }
    public DateTimeOffset? RecoveredAtUtc { get; set; }
    public DateTimeOffset? NextRecoveryAttemptAtUtc { get; set; }
    public long? SupersedingRevision { get; set; }
    public int RecoveryAttemptCount { get; set; }
    public string DiagnosticCode { get; set; } = string.Empty;
    public bool BlocksPublishing { get; set; }
    public bool BlocksMerge { get; set; }
    public bool BlocksLogin { get; set; }
    public bool BlocksGarbageCollection { get; set; }
    public bool BlocksLifecycle { get; set; }
}
