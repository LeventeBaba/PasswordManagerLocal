using PasswordManagerLocal.Common.Contracts.Notifications;

namespace PasswordManagerLocal.Common.Contracts.Runtime;

/// <summary>Metadata-only change hint forwarded to one live frontend session.</summary>
public sealed record FrontendChangeStreamDelivery(
    Guid RuntimeInstanceId,
    long Sequence,
    FrontendChangeNotification? Notification,
    bool RequiresAuthoritativeRefresh);
