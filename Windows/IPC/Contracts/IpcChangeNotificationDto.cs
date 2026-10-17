using PasswordManagerLocal.Common.Contracts.Notifications;

namespace PasswordManagerLocal.Windows.Ipc.Contracts;

/// <summary>
/// Metadata-only invalidation sent to the registered UI. It deliberately has no user ID,
/// entity data, credentials, or other domain payload.
/// </summary>
public sealed record IpcChangeNotificationDto(
    Guid RuntimeInstanceId,
    long Sequence,
    FrontendChangeScope Scope,
    DateTimeOffset OccurredAtUtc,
    bool RequiresAuthoritativeRefresh);
