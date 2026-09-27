namespace PasswordManagerLocal.Windows.Ipc.Contracts;

public sealed record DatabaseResetResultDto(
    bool Completed,
    bool RequiresProcessRestart,
    string? SafeMessage);
