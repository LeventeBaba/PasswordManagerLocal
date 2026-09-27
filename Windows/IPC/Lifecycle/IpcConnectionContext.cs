using PasswordManagerLocal.Windows.Ipc.Protocol;

namespace PasswordManagerLocal.Windows.Ipc.Lifecycle;

public sealed record IpcConnectionContext(
    Guid ConnectionId,
    IpcPeerRole PeerRole,
    int PeerProcessId,
    int PeerWindowsSessionId,
    Guid PeerSessionId,
    IpcCapabilities PeerCapabilities);
