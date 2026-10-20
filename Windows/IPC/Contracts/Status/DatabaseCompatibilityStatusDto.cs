namespace PasswordManagerLocal.Windows.Ipc.Contracts.Status;

public sealed record DatabaseCompatibilityStatusDto(
    int? DetectedVersion,
    int OldestSupportedVersion,
    int CurrentVersion);
