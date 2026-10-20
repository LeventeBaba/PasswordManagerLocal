using PasswordManagerLocal.Windows.Ipc.Contracts;

namespace PasswordManagerLocal.Windows.Ipc.Abstractions.Server;

public interface IAgentExitRequestSink
{
    Task<bool> RequestExitAsync(
        AgentExitRequestDto request,
        CancellationToken cancellationToken);
}
