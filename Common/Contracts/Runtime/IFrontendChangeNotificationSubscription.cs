namespace PasswordManagerLocal.Common.Contracts.Runtime;

/// <summary>
/// One bounded frontend-session subscription. Unsubscribe must synchronously revoke delivery;
/// DisposeAsync may then finish asynchronous reader cleanup.
/// </summary>
public interface IFrontendChangeNotificationSubscription : IAsyncDisposable
{
    FrontendChangeStreamStart Start { get; }

    IAsyncEnumerable<FrontendChangeStreamDelivery> ReadAllAsync(
        CancellationToken cancellationToken = default);

    void Unsubscribe();
}
