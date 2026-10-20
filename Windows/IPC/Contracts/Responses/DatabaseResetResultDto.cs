namespace PasswordManagerLocal.Windows.Ipc.Contracts.Responses;

public sealed record DatabaseResetResultDto(
    bool Completed,
    bool RequiresProcessRestart,
    string? SafeMessage);
