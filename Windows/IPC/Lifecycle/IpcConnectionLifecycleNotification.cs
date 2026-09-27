namespace PasswordManagerLocal.Windows.Ipc.Lifecycle;

public sealed record IpcConnectionLifecycleNotification(
    Guid ConnectionId,
    IpcConnectionLifecycleState State,
    IpcConnectionContext? Context,
    IpcDisconnectKind DisconnectKind,
    DateTimeOffset OccurredAtUtc);
