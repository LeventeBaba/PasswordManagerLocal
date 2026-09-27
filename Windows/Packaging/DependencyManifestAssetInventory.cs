using System.Text.Json;

namespace PasswordManagerLocal.Windows.Packaging;

internal static class DependencyManifestAssetInventory
{
    private static readonly string[] AssetPropertyNames =
    [
        "runtime",
        "native",
        "resources",
        "runtimeTargets"
    ];

    public static HashSet<string> ReadOwnedFileNames(IEnumerable<string> manifestPaths)
    {
        ArgumentNullException.ThrowIfNull(manifestPaths);
        var owned = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var manifestPath in manifestPaths)
        {
            if (string.IsNullOrWhiteSpace(manifestPath))
                throw new ArgumentException("Dependency manifest path must not be empty.", nameof(manifestPaths));
            AddManifestAssets(Path.GetFullPath(manifestPath), owned);
        }
        return owned;
    }

    private static void AddManifestAssets(string manifestPath, ISet<string> owned)
    {
        if (!File.Exists(manifestPath))
            throw new FileNotFoundException("Dependency manifest does not exist.", manifestPath);

        using var document = JsonDocument.Parse(File.ReadAllText(manifestPath));
        if (!document.RootElement.TryGetProperty("targets", out var targets) ||
            targets.ValueKind != JsonValueKind.Object)
        {
            throw new InvalidOperationException($"Manifest has no targets object: {manifestPath}");
        }

        var runtimeTarget = targets.EnumerateObject()
            .FirstOrDefault(property => property.Name.Contains("/win-x64", StringComparison.OrdinalIgnoreCase));
        if (runtimeTarget.Equals(default(JsonProperty)))
            throw new InvalidOperationException($"No win-x64 target was found in manifest: {manifestPath}");

        foreach (var library in runtimeTarget.Value.EnumerateObject())
        {
            foreach (var propertyName in AssetPropertyNames)
            {
                if (!library.Value.TryGetProperty(propertyName, out var assets) ||
                    assets.ValueKind != JsonValueKind.Object)
                {
                    continue;
                }

                foreach (var asset in assets.EnumerateObject())
                {
                    var fileName = Path.GetFileName(asset.Name.Replace('/', Path.DirectorySeparatorChar));
                    if (fileName.Length == 0 || fileName.Equals("_._", StringComparison.Ordinal))
                        continue;
                    owned.Add(fileName);
                }
            }
        }
    }
}
