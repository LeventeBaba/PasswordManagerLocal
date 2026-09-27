using PasswordManagerLocal.Windows.Ipc.Contracts;

namespace PasswordManagerLocal.Windows.Ipc.Server;

public interface IAgentExitRequestSink
{
    Task<bool> RequestExitAsync(
        AgentExitRequestDto request,
        CancellationToken cancellationToken);
}
