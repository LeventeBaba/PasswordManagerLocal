namespace PasswordManagerLocal.Windows.Ipc.Contracts;

public sealed record SynchronizationStatusDto(
    SynchronizationStatusState State,
    IpcFailureDto? LastFailure);
