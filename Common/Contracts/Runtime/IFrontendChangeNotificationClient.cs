namespace PasswordManagerLocal.Common.Contracts.Runtime;

/// <summary>Exposes change hints only while an authorized frontend attachment is ready.</summary>
public interface IFrontendChangeNotificationClient
{
    /// <summary>Raised after the active runtime's change source is replaced or restored.</summary>
    event EventHandler? ChangeSourceChanged;

    /// <summary>
    /// Registers one bounded stream. A null user ID receives global changes only; a user ID
    /// receives that user's changes and global changes according to the backend source contract.
    /// Callers must perform an authoritative reconciliation from <see cref="IFrontendChangeNotificationSubscription.Start"/>.
    /// </summary>
    IFrontendChangeNotificationSubscription SubscribeChangeNotifications(
        Guid? userId = null,
        int capacity = 64);
}
