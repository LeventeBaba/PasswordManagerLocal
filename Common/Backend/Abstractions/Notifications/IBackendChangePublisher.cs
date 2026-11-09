using PasswordManagerLocal.Common.Contracts.Notifications;

namespace PasswordManagerLocal.Common.Backend.Abstractions.Notifications;

/// <summary>Publishes metadata-only invalidation hints for one backend runtime.</summary>
public interface IBackendChangePublisher
{
    FrontendChangeNotification? Publish(FrontendChangeScope scope, Guid? userId = null);
}
