namespace PasswordManagerLocal.Common.Backend.Abstractions.Notifications;

public interface IBackendChangeSubscription : IAsyncDisposable
{
    BackendChangeSubscriptionStart Start { get; }

    IAsyncEnumerable<BackendChangeDelivery> ReadAllAsync(CancellationToken cancellationToken = default);
}
