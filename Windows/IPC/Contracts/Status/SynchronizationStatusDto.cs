namespace PasswordManagerLocal.Windows.Ipc.Contracts.Status;

public sealed record SynchronizationStatusDto(
    SynchronizationStatusState State,
    IpcFailureDto? LastFailure);
