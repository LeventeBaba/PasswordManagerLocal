namespace PasswordManagerLocal.Common.Backend.Models;

/// <summary>
/// Non-secret evidence used to create or refresh a durable synchronization-health record.
/// The parameterless form keeps call sites readable for large scoped diagnostics, while the
/// positional constructor is convenient for compact canonical-health failures.
/// </summary>
public sealed record UserSyncFaultDescriptor
{
    public UserSyncFaultDescriptor() { }

    public UserSyncFaultDescriptor(
        Guid userId,
        UserSyncFaultScope scope,
        UserSyncFaultKind kind,
        UserSyncHealthStatus status,
        string diagnosticCode)
    {
        UserId = userId;
        Scope = scope;
        Kind = kind;
        Status = status;
        DiagnosticCode = diagnosticCode;
    }

    public Guid UserId { get; init; }
    public UserSyncFaultScope Scope { get; init; }
    public UserSyncFaultKind Kind { get; init; }
    public UserSyncHealthStatus Status { get; init; }
    public string DiagnosticCode { get; init; } = string.Empty;
    public string AffectedComponent { get; init; } = string.Empty;
    public Guid? OriginDeviceId { get; init; }
    public Guid? OriginInstanceId { get; init; }
    public long? KeyEpoch { get; init; }
    public long? MembershipEpoch { get; init; }
    public long? OriginRevision { get; init; }
    public byte[] ExpectedHash { get; init; } = [];
    public byte[] ObservedHash { get; init; } = [];
    public byte[] ConflictingHash { get; init; } = [];
    public bool BlocksPublishing { get; init; }
    public bool BlocksMerge { get; init; }
    public bool BlocksLogin { get; init; }
    public bool BlocksGarbageCollection { get; init; }
    public bool BlocksLifecycle { get; init; }
}
