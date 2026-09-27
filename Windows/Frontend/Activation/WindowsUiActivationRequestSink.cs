using PasswordManagerLocal.Windows.EndpointRpc.Client;
using PasswordManagerLocal.Windows.Ipc.Contracts;
using PasswordManagerLocal.Windows.Ipc.Server;

namespace PasswordManagerLocal.Windows.Frontend.Activation;

public sealed class WindowsUiActivationRequestSink : IUiActivationRequestSink
{
    private readonly IWindowsWindowActivationBridge _activationBridge;
    private readonly IWindowsUiShutdownBridge _shutdownBridge;
    private readonly IIntentionalAgentShutdownCoordinator _shutdownCoordinator;
    private readonly object _shutdownGate = new();
    private Task<bool>? _shutdownTask;

    public WindowsUiActivationRequestSink(
        IWindowsWindowActivationBridge activationBridge,
        IWindowsUiShutdownBridge shutdownBridge,
        IIntentionalAgentShutdownCoordinator shutdownCoordinator)
    {
        _activationBridge = activationBridge ?? throw new ArgumentNullException(nameof(activationBridge));
        _shutdownBridge = shutdownBridge ?? throw new ArgumentNullException(nameof(shutdownBridge));
        _shutdownCoordinator = shutdownCoordinator ?? throw new ArgumentNullException(nameof(shutdownCoordinator));
    }

    public Task<bool> RequestActivationAsync(
        UiActivationRequestDto request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (request.Command != UiActivationCommand.IntentionalAgentShutdown)
            return _activationBridge.ActivateAsync(cancellationToken);

        Task<bool> shutdownTask;
        lock (_shutdownGate)
            shutdownTask = _shutdownTask ??= BeginIntentionalShutdownAsync(cancellationToken);
        return ObserveShutdownResultAsync(shutdownTask);
    }

    private async Task<bool> BeginIntentionalShutdownAsync(CancellationToken cancellationToken)
    {
        var suppressionInstalled = false;
        try
        {
            // Acknowledgement is returned only after suppression and both backend channels are closed.
            suppressionInstalled = await _shutdownCoordinator
                .BeginIntentionalAgentShutdownAsync(cancellationToken);
            if (!suppressionInstalled)
                return false;

            var scheduled = await _shutdownBridge.ShutdownAsync(cancellationToken);
            if (scheduled)
                return true;

            await _shutdownCoordinator.CancelIntentionalAgentShutdownAsync(CancellationToken.None);
            return false;
        }
        catch
        {
            if (suppressionInstalled)
            {
                try
                {
                    await _shutdownCoordinator.CancelIntentionalAgentShutdownAsync(CancellationToken.None);
                }
                catch
                {
                }
            }
            throw;
        }
    }

    private async Task<bool> ObserveShutdownResultAsync(Task<bool> shutdownTask)
    {
        try
        {
            var result = await shutdownTask;
            if (!result)
            {
                lock (_shutdownGate)
                {
                    if (ReferenceEquals(_shutdownTask, shutdownTask))
                        _shutdownTask = null;
                }
            }
            return result;
        }
        catch
        {
            lock (_shutdownGate)
            {
                if (ReferenceEquals(_shutdownTask, shutdownTask))
                    _shutdownTask = null;
            }
            throw;
        }
    }
}
