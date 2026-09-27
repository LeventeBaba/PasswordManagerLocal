namespace PasswordManagerLocal.Windows.Ipc.Lifecycle;

public sealed record UiConnectionRegistration(
    Guid ConnectionId,
    int ProcessId,
    int WindowsSessionId,
    Guid InstanceId,
    long Generation);
