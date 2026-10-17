namespace PasswordManagerLocal.Common.Backend.Abstractions.Notifications;

/// <summary>
/// The atomic registration watermark. RequiresAuthoritativeRefresh is true for a new
/// subscription because this process keeps no replay history.
/// </summary>
public sealed record BackendChangeSubscriptionStart(
    Guid RuntimeInstanceId,
    long Sequence,
    bool RequiresAuthoritativeRefresh);
