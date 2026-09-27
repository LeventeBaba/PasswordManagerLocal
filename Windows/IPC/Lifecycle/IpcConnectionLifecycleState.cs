namespace PasswordManagerLocal.Windows.Ipc.Lifecycle;

public enum IpcConnectionLifecycleState
{
    Connected = 1,
    HandshakeCompleted = 2,
    Disconnected = 3,
    Faulted = 4
}
