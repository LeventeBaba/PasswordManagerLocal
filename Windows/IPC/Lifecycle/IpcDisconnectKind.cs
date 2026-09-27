namespace PasswordManagerLocal.Windows.Ipc.Lifecycle;

public enum IpcDisconnectKind
{
    None = 0,
    Clean = 1,
    TransportFailure = 2,
    ProtocolFailure = 3,
    ServerCancellation = 4
}
