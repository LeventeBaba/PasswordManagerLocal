using PasswordManagerLocal.Common.Contracts.Notifications;

namespace PasswordManagerLocal.Common.Backend.Abstractions.Notifications;

/// <summary>
/// A matching notification or an explicit refresh marker. Sequence numbers are global to a
/// runtime, so filtered subscribers must use RequiresAuthoritativeRefresh rather than infer
/// loss from gaps between notifications.
/// </summary>
public sealed record BackendChangeDelivery(
    Guid RuntimeInstanceId,
    long Sequence,
    FrontendChangeNotification? Notification,
    bool RequiresAuthoritativeRefresh);
