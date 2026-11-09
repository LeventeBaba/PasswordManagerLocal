using PasswordManagerLocal.Windows.Ipc.Protocol;

namespace PasswordManagerLocal.Windows.Ipc.Contracts.Handshake;

public sealed record IpcHandshakeRequest(
    IpcPeerRole ClientRole,
    int ProcessId,
    int WindowsSessionId,
    Guid SessionId,
    IpcCapabilities Capabilities);
