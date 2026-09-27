namespace PasswordManagerLocal.Windows.EndpointRpc.Client;

public interface IIntentionalAgentShutdownCoordinator
{
    Task<bool> BeginIntentionalAgentShutdownAsync(CancellationToken cancellationToken = default);
    Task CancelIntentionalAgentShutdownAsync(CancellationToken cancellationToken = default);
}
