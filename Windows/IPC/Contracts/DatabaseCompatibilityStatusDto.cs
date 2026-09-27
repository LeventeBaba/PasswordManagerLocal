namespace PasswordManagerLocal.Windows.Ipc.Contracts;

public sealed record DatabaseCompatibilityStatusDto(
    int? DetectedVersion,
    int OldestSupportedVersion,
    int CurrentVersion);
