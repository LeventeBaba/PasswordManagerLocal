namespace PasswordManagerLocal.Windows.Agent.Background;

public sealed record WindowsCurrentUserRegistryValue(
    bool EntryExists,
    string? StringValue);
