namespace PasswordManagerLocal.Windows.Packaging;

public sealed class ManifestOwnedDllPruner
{
    // Intentionally empty. If a future feature loads a DLL dynamically and the
    // SDK therefore cannot represent it in .deps.json, add that filename here
    // only together with a targeted architecture/runtime test that proves why
    // the non-manifest assembly is required.
    private static readonly HashSet<string> ExplicitNonManifestDllAllowList =
        new(StringComparer.OrdinalIgnoreCase);

    internal static bool IsExplicitlyAllowed(string fileName) =>
        ExplicitNonManifestDllAllowList.Contains(fileName);

    public IReadOnlyList<string> Prune(
        string directory,
        IEnumerable<string> manifestPaths,
        string? reportPath = null)
    {
        var root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(directory));
        if (!Directory.Exists(root))
            throw new DirectoryNotFoundException($"DLL-prune directory does not exist: {root}");

        var owned = DependencyManifestAssetInventory.ReadOwnedFileNames(manifestPaths);
        var removed = Directory.EnumerateFiles(root, "*.dll", SearchOption.AllDirectories)
            .Where(path =>
                !owned.Contains(Path.GetFileName(path)) &&
                !ExplicitNonManifestDllAllowList.Contains(Path.GetFileName(path)))
            .Select(path => new
            {
                AbsolutePath = path,
                RelativePath = Path.GetRelativePath(root, path).Replace(Path.DirectorySeparatorChar, '/')
            })
            .OrderBy(file => file.RelativePath, StringComparer.OrdinalIgnoreCase)
            .ToArray();

        foreach (var file in removed)
            File.Delete(file.AbsolutePath);

        if (!string.IsNullOrWhiteSpace(reportPath))
        {
            var fullReportPath = Path.GetFullPath(reportPath);
            var reportDirectory = Path.GetDirectoryName(fullReportPath);
            if (!string.IsNullOrEmpty(reportDirectory))
                Directory.CreateDirectory(reportDirectory);
            File.WriteAllLines(fullReportPath, removed.Select(file => file.RelativePath));
        }

        return removed.Select(file => file.RelativePath).ToArray();
    }
}
