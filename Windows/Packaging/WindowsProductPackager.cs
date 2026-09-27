namespace PasswordManagerLocal.Windows.Packaging;

public sealed class WindowsProductPackager
{
    private readonly WindowsProductValidator _validator;
    private readonly DeterministicDirectoryMerger _merger;

    public WindowsProductPackager(
        WindowsProductValidator? validator = null,
        DeterministicDirectoryMerger? merger = null)
    {
        _validator = validator ?? new WindowsProductValidator();
        _merger = merger ?? new DeterministicDirectoryMerger();
    }

    public PackageReport Package(PackageOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        _validator.ValidateFrontendStage(options.FrontendDirectory);
        _validator.ValidateAgentStage(options.AgentDirectory);

        var reportRoot = Path.GetFullPath(options.ReportDirectory);
        var outputRoot = Path.GetFullPath(options.OutputDirectory);
        if (PathsOverlap(reportRoot, outputRoot))
            throw new InvalidOperationException("Packaging reports and the final product directory must be separate and non-nested.");

        var merge = _merger.Merge(
            options.FrontendDirectory,
            options.AgentDirectory,
            options.OutputDirectory);
        var report = new PackageReport(
            SchemaVersion: "1.0",
            RuntimeIdentifier: options.RuntimeIdentifier,
            TargetFramework: options.TargetFramework,
            Summary: merge.Summary,
            Baseline: BaselineTreeAnalyzer.Analyze(options.BaselineTreePath, options.BaselineTotalBytes),
            Files: merge.Files);

        _validator.ValidateFinalProduct(options.OutputDirectory, report);
        PackageReportWriter.Write(report, options.ReportDirectory);
        return report;
    }

    private static bool PathsOverlap(string left, string right)
    {
        var leftRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(left));
        var rightRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(right));
        if (string.Equals(leftRoot, rightRoot, StringComparison.OrdinalIgnoreCase))
            return true;
        var leftPrefix = leftRoot + Path.DirectorySeparatorChar;
        var rightPrefix = rightRoot + Path.DirectorySeparatorChar;
        return leftPrefix.StartsWith(rightPrefix, StringComparison.OrdinalIgnoreCase) ||
               rightPrefix.StartsWith(leftPrefix, StringComparison.OrdinalIgnoreCase);
    }
}
