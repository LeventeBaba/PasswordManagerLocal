namespace PasswordManagerLocal.Windows.Packaging;

public sealed record MergeResult(
    IReadOnlyList<PackageFileEntry> Files,
    PackageSummary Summary);
