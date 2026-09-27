namespace PasswordManagerLocal.Windows.Agent.Background;

public sealed record WindowsStartupRegistrationSnapshot(
    bool EntryExists,
    bool IsRegistered,
    string? Command);
