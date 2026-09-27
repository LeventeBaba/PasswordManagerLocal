namespace PasswordManagerLocal.Windows.Packaging;

public sealed record PackageReport(
    string SchemaVersion,
    string RuntimeIdentifier,
    string TargetFramework,
    PackageSummary Summary,
    BaselineAnalysis? Baseline,
    IReadOnlyList<PackageFileEntry> Files);
