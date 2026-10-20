using PasswordManagerLocal.Windows.Ipc.Protocol;

namespace PasswordManagerLocal.Windows.Ipc.Contracts;

public sealed record IpcHandshakeResponse(
    bool Accepted,
    IpcPeerRole ServerRole,
    Guid ConnectionId,
    IpcCapabilities Capabilities,
    IpcError? Error);
