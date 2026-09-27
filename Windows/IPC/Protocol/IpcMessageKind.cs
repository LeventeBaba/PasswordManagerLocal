namespace PasswordManagerLocal.Windows.Ipc.Protocol;

public enum IpcMessageKind
{
    HandshakeRequest = 1,
    HandshakeResponse = 2,
    Request = 3,
    Response = 4,
    RequestCancellation = 5
}
