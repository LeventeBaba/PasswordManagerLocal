namespace PasswordManagerLocal.Windows.Packaging;

public sealed record PackageOptions(
    string FrontendDirectory,
    string AgentDirectory,
    string OutputDirectory,
    string ReportDirectory,
    string? BaselineTreePath = null,
    long? BaselineTotalBytes = null,
    string RuntimeIdentifier = "win-x64",
    string TargetFramework = "net10.0-windows");
