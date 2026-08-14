namespace PasswordManagerLocal.Windows.Packaging;

public sealed record StagedCollision(
    string RelativePath,
    long FrontendLength,
    string FrontendSha256,
    long AgentLength,
    string AgentSha256,
    bool CanResolveWithCopyUsed,
    string? AssemblyName);
