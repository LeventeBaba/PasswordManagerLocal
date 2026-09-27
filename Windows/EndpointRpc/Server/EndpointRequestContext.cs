using PasswordManagerLocal.Windows.EndpointRpc.Contracts;
using PasswordManagerLocal.Windows.Ipc.Protocol;

namespace PasswordManagerLocal.Windows.EndpointRpc.Server;

public sealed record EndpointRequestContext(
    Guid ConnectionId,
    long CorrelationId,
    EndpointOperationId OperationId,
    IpcPeerRole PeerRole,
    int PeerProcessId,
    Guid PeerSessionId,
    CancellationToken CancellationToken)
{
    internal EndpointInvocationTracker Invocation { get; init; } = new();
}
