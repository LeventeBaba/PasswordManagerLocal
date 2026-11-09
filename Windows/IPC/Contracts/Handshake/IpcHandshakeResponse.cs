using PasswordManagerLocal.Windows.Ipc.Protocol;

namespace PasswordManagerLocal.Windows.Ipc.Contracts.Handshake;

public sealed record IpcHandshakeResponse(
    bool Accepted,
    IpcPeerRole ServerRole,
    Guid ConnectionId,
    IpcCapabilities Capabilities,
    IpcError? Error);
