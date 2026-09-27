using PasswordManagerLocal.Windows.Ipc.Contracts;
using PasswordManagerLocal.Windows.Ipc.Server;

namespace PasswordManagerLocal.Windows.Agent.Hosting;

public sealed class WindowsAgentExitRequestSink : IAgentExitRequestSink
{
    private readonly WindowsAgentShutdownCoordinator _shutdownCoordinator;
    private readonly TimeSpan _responseGracePeriod;
    private int _requestStarted;

    public WindowsAgentExitRequestSink(
        WindowsAgentShutdownCoordinator shutdownCoordinator,
        TimeSpan? responseGracePeriod = null)
    {
        _shutdownCoordinator = shutdownCoordinator
            ?? throw new ArgumentNullException(nameof(shutdownCoordinator));
        _responseGracePeriod = responseGracePeriod ?? TimeSpan.FromMilliseconds(100);
        if (_responseGracePeriod < TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(responseGracePeriod));
    }

    public Task<bool> RequestExitAsync(
        AgentExitRequestDto request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        cancellationToken.ThrowIfCancellationRequested();
        var reason = MapReason(request.Reason);
        if (Interlocked.Exchange(ref _requestStarted, 1) == 0)
            _ = RequestShutdownAfterResponseAsync(reason);
        return Task.FromResult(true);
    }

    private async Task RequestShutdownAfterResponseAsync(WindowsAgentShutdownReason reason)
    {
        if (_responseGracePeriod > TimeSpan.Zero)
            await Task.Delay(_responseGracePeriod);
        _shutdownCoordinator.RequestShutdown(reason);
    }

    private WindowsAgentShutdownReason MapReason(AgentExitReason reason) => reason switch
    {
        AgentExitReason.UserRequested => WindowsAgentShutdownReason.UserRequestedExit,
        AgentExitReason.ApplicationUpdate => WindowsAgentShutdownReason.RestartRequired,
        AgentExitReason.ProcessRestartRequired => WindowsAgentShutdownReason.RestartRequired,
        _ => throw new ArgumentOutOfRangeException(nameof(reason))
    };
}
