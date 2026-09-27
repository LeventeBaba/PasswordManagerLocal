using System.Text;
using System.Text.Json;

namespace PasswordManagerLocal.Windows.Packaging;

public sealed class WindowsProductValidator
{
    private static readonly string[] FrontendRequiredFiles =
    [
        "PasswordManagerLocal.exe",
        "PasswordManagerLocal.dll",
        "PasswordManagerLocal.deps.json",
        "PasswordManagerLocal.runtimeconfig.json",
        "PasswordManagerLocal.Common.Frontend.dll",
        "PasswordManagerLocal.Common.Contracts.dll",
        "PasswordManagerLocal.Common.Preferences.dll",
        "PasswordManagerLocal.Windows.EndpointRpc.Contracts.dll",
        "PasswordManagerLocal.Windows.EndpointRpc.Client.dll",
        "PasswordManagerLocal.Windows.Ipc.dll",
        "Avalonia.Win32.dll",
        "Avalonia.Skia.dll",
        "ConfigureWindowsFirewall.ps1",
        "ConfigureWindowsFirewall.bat"
    ];

    private static readonly string[] AgentRequiredFiles =
    [
        "PasswordManagerLocal.Windows.Agent.exe",
        "PasswordManagerLocal.Windows.Agent.dll",
        "PasswordManagerLocal.Windows.Agent.deps.json",
        "PasswordManagerLocal.Windows.Agent.runtimeconfig.json",
        "PasswordManagerLocal.Common.Backend.dll",
        "PasswordManagerLocal.Common.Backend.Hosting.dll",
        "PasswordManagerLocal.Common.Contracts.dll",
        "PasswordManagerLocal.Common.Preferences.dll",
        "PasswordManagerLocal.Windows.Backend.dll",
        "PasswordManagerLocal.Windows.EndpointRpc.Contracts.dll",
        "PasswordManagerLocal.Windows.EndpointRpc.Server.dll",
        "PasswordManagerLocal.Windows.Ipc.dll",
        "Microsoft.EntityFrameworkCore.dll",
        "Microsoft.EntityFrameworkCore.Relational.dll",
        "Microsoft.EntityFrameworkCore.Sqlite.dll",
        "Microsoft.Data.Sqlite.dll",
        "SQLitePCLRaw.core.dll",
        "SQLitePCLRaw.batteries_v2.dll",
        "SQLitePCLRaw.provider.e_sqlcipher.dll",
        "e_sqlcipher.dll",
        "NSec.Cryptography.dll",
        "libsodium.dll",
        "Google.Protobuf.dll",
        "Assets/app_icon.ico"
    ];

    private static readonly string[] FrontendForbiddenDependencies =
    [
        "PasswordManagerLocal.Common.Backend",
        "PasswordManagerLocal.Common.Backend.Hosting",
        "PasswordManagerLocal.Windows.Backend",
        "PasswordManagerLocal.Windows.EndpointRpc.Server",
        "Microsoft.EntityFrameworkCore",
        "Microsoft.Data.Sqlite",
        "SQLitePCLRaw"
    ];

    private static readonly string[] AgentForbiddenDependencies =
    [
        "PasswordManagerLocal.Common.Frontend",
        "Avalonia",
        "ReactiveUI",
        "PresentationFramework",
        "System.Windows.Forms",
        "WindowsBase"
    ];

    public void ValidateFrontendStage(string directory)
    {
        var root = RequireDirectory(directory, "frontend staging");
        AssertRequired(root, FrontendRequiredFiles, "frontend staging");
        AssertAbsent(root, [
            "PasswordManagerLocal.Windows.Agent.exe",
            "PasswordManagerLocal.Windows.Agent.dll",
            "PasswordManagerLocal.Common.Backend.dll",
            "PasswordManagerLocal.Windows.Backend.dll",
            "PasswordManagerLocal.Windows.EndpointRpc.Server.dll"
        ], "frontend staging");
        AssertNoDevelopmentArtifacts(root);
        var frontendManifest = Path.Combine(root, "PasswordManagerLocal.deps.json");
        AssertManifestBoundary(frontendManifest, FrontendForbiddenDependencies, forbidden: true);
        AssertAllDllsOwnedByManifests(root, [frontendManifest], "frontend staging");
        AssertRuntimeConfigurationTrimmed(Path.Combine(root, "PasswordManagerLocal.runtimeconfig.json"), "frontend");
        AssertBinaryContains(Path.Combine(root, "PasswordManagerLocal.Common.Frontend.dll"), "LocalizationJsonContext");
        AssertExcludedRuntimeTools(root);
    }

    public void ValidateAgentStage(string directory)
    {
        var root = RequireDirectory(directory, "Agent staging");
        AssertRequired(root, AgentRequiredFiles, "Agent staging");
        AssertAbsent(root, [
            "PasswordManagerLocal.exe",
            "PasswordManagerLocal.dll",
            "PasswordManagerLocal.Common.Frontend.dll",
            "Avalonia.dll",
            "Avalonia.Base.dll",
            "Avalonia.Controls.dll",
            "Avalonia.Win32.dll",
            "ReactiveUI.dll",
            "System.Windows.Forms.dll",
            "System.Windows.dll",
            "PresentationCore.dll",
            "PresentationFramework.dll",
            "WindowsBase.dll",
            "Microsoft.VisualBasic.dll",
            "System.Web.dll",
            "System.ServiceModel.Web.dll",
            "System.Xml.XmlSerializer.dll"
        ], "Agent staging");
        AssertNoDevelopmentArtifacts(root);
        var agentManifest = Path.Combine(root, "PasswordManagerLocal.Windows.Agent.deps.json");
        AssertManifestBoundary(agentManifest, AgentForbiddenDependencies, forbidden: true);
        AssertManifestBoundary(agentManifest, [
            "PasswordManagerLocal.Common.Backend",
            "PasswordManagerLocal.Windows.EndpointRpc.Server",
            "Microsoft.EntityFrameworkCore.Sqlite"
        ], forbidden: false);
        AssertAllDllsOwnedByManifests(root, [agentManifest], "Agent staging");
        AssertRuntimeConfigurationTrimmed(Path.Combine(root, "PasswordManagerLocal.Windows.Agent.runtimeconfig.json"), "Agent");
        AssertExcludedRuntimeTools(root);

        var agentAssembly = Path.Combine(root, "PasswordManagerLocal.Windows.Agent.dll");
        AssertBinaryContains(agentAssembly, "Localization.en_us.json");
        AssertBinaryContains(agentAssembly, "Localization.hu.json");
        AssertBinaryContains(Path.Combine(root, "PasswordManagerLocal.Common.Backend.dll"), "BackendJsonSerializerContext");
        AssertBinaryContains(Path.Combine(root, "PasswordManagerLocal.Common.Backend.Hosting.dll"), "BackgroundSyncSettingsJsonContext");
        AssertBinaryContains(Path.Combine(root, "PasswordManagerLocal.Common.Preferences.dll"), "ApplicationPreferencesJsonContext");
        AssertBinaryContains(Path.Combine(root, "PasswordManagerLocal.Windows.Ipc.dll"), "WindowsIpcJsonContext");
        AssertBinaryContains(Path.Combine(root, "PasswordManagerLocal.Windows.EndpointRpc.Contracts.dll"), "EndpointRpcJsonContext");

        // Runtime configuration plus the explicit absence of known unused desktop,
        // Visual Basic, legacy web, and XML serializer assemblies above guards
        // against an accidentally untrimmed Agent publication.
    }

    public void ValidateFinalProduct(
        string directory,
        PackageReport report)
    {
        var root = RequireDirectory(directory, "final product");
        AssertRequired(root, FrontendRequiredFiles.Concat(AgentRequiredFiles).Distinct(StringComparer.OrdinalIgnoreCase), "final product");
        if (Directory.Exists(Path.Combine(root, "AgentRuntime")))
            throw new InvalidOperationException("The final product contains the prohibited AgentRuntime directory.");
        AssertNoDevelopmentArtifacts(root);

        var frontendExe = Directory.EnumerateFiles(root, "PasswordManagerLocal.exe", SearchOption.AllDirectories).ToArray();
        var agentExe = Directory.EnumerateFiles(root, "PasswordManagerLocal.Windows.Agent.exe", SearchOption.AllDirectories).ToArray();
        if (frontendExe.Length != 1 || agentExe.Length != 1 ||
            !string.Equals(Path.GetDirectoryName(frontendExe[0]), root, StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(Path.GetDirectoryName(agentExe[0]), root, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("Both primary executables must exist exactly once as siblings in the final product root.");
        }

        var frontendDeps = Path.Combine(root, "PasswordManagerLocal.deps.json");
        var agentDeps = Path.Combine(root, "PasswordManagerLocal.Windows.Agent.deps.json");
        AssertManifestBoundary(frontendDeps, FrontendForbiddenDependencies, forbidden: true);
        AssertManifestBoundary(agentDeps, AgentForbiddenDependencies, forbidden: true);
        AssertManifestDoesNotContainLegacyPath(frontendDeps);
        AssertManifestDoesNotContainLegacyPath(agentDeps);
        AssertManifestAssetsExist(root, frontendDeps);
        AssertManifestAssetsExist(root, agentDeps);
        AssertAllDllsOwnedByManifests(root, [frontendDeps, agentDeps], "final product");

        var files = Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories).ToArray();
        var bytes = files.Sum(static path => new FileInfo(path).Length);
        if (files.Length != report.Summary.FinalFileCount || bytes != report.Summary.FinalSizeBytes)
        {
            throw new InvalidOperationException(
                $"Final product totals differ from the packaging report. " +
                $"Report: {report.Summary.FinalFileCount} files/{report.Summary.FinalSizeBytes} bytes; " +
                $"filesystem: {files.Length} files/{bytes} bytes.");
        }
    }

    private static string RequireDirectory(string path, string scope)
    {
        var fullPath = Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
        if (!Directory.Exists(fullPath))
            throw new DirectoryNotFoundException($"The {scope} directory does not exist: {fullPath}");
        return fullPath;
    }

    private static void AssertRequired(string root, IEnumerable<string> relativePaths, string scope)
    {
        var missing = relativePaths
            .Select(path => path.Replace('/', Path.DirectorySeparatorChar))
            .Where(path => !File.Exists(Path.Combine(root, path)))
            .OrderBy(static path => path, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        if (missing.Length != 0)
            throw new InvalidOperationException($"The {scope} is missing: {string.Join(", ", missing)}");
    }

    private static void AssertAbsent(string root, IEnumerable<string> fileNames, string scope)
    {
        var forbidden = new HashSet<string>(fileNames, StringComparer.OrdinalIgnoreCase);
        var found = Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories)
            .Where(path => forbidden.Contains(Path.GetFileName(path)))
            .ToArray();
        if (found.Length != 0)
            throw new InvalidOperationException($"The {scope} contains forbidden files: {string.Join(", ", found)}");
    }

    private static void AssertNoDevelopmentArtifacts(string root)
    {
        var invalid = Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories)
            .Where(path =>
            {
                var name = Path.GetFileName(path);
                return name.EndsWith(".pdb", StringComparison.OrdinalIgnoreCase) ||
                       name.EndsWith(".xml", StringComparison.OrdinalIgnoreCase) ||
                       name.Contains("testhost", StringComparison.OrdinalIgnoreCase) ||
                       name.Contains("Microsoft.TestPlatform", StringComparison.OrdinalIgnoreCase) ||
                       name.Contains(".Tests.", StringComparison.OrdinalIgnoreCase) ||
                       name.Equals("packaging-report.json", StringComparison.OrdinalIgnoreCase) ||
                       name.Equals("packaging-report.txt", StringComparison.OrdinalIgnoreCase);
            })
            .ToArray();
        if (invalid.Length != 0)
            throw new InvalidOperationException($"Development/report artifacts were shipped: {string.Join(", ", invalid)}");

        var invalidDirectories = Directory.EnumerateDirectories(root, "*", SearchOption.AllDirectories)
            .Where(path => Path.GetFileName(path).Equals("bin", StringComparison.OrdinalIgnoreCase) ||
                           Path.GetFileName(path).Equals("obj", StringComparison.OrdinalIgnoreCase) ||
                           Path.GetFileName(path).Equals("staging", StringComparison.OrdinalIgnoreCase) ||
                           Path.GetFileName(path).Equals("reports", StringComparison.OrdinalIgnoreCase))
            .ToArray();
        if (invalidDirectories.Length != 0)
            throw new InvalidOperationException($"Build/staging directories were shipped: {string.Join(", ", invalidDirectories)}");
    }

    private static void AssertManifestBoundary(string path, IEnumerable<string> values, bool forbidden)
    {
        var text = File.ReadAllText(path);
        foreach (var value in values)
        {
            var contains = text.Contains(value, StringComparison.Ordinal);
            if (forbidden && contains)
                throw new InvalidOperationException($"Manifest '{path}' contains forbidden dependency '{value}'.");
            if (!forbidden && !contains)
                throw new InvalidOperationException($"Manifest '{path}' is missing required dependency '{value}'.");
        }
    }

    private static void AssertManifestDoesNotContainLegacyPath(string path)
    {
        if (File.ReadAllText(path).Contains("AgentRuntime", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException($"Manifest still references AgentRuntime: {path}");
    }

    private static void AssertRuntimeConfigurationTrimmed(string path, string scope)
    {
        using var document = JsonDocument.Parse(File.ReadAllText(path));
        var runtimeOptions = document.RootElement.GetProperty("runtimeOptions");
        if (!runtimeOptions.TryGetProperty("includedFrameworks", out _))
            throw new InvalidOperationException($"The {scope} runtime configuration is not self-contained: {path}");
        if (!runtimeOptions.TryGetProperty("configProperties", out var properties))
            throw new InvalidOperationException($"The {scope} runtime configuration has no trim feature switches: {path}");

        AssertFalseProperty(properties, "System.Reflection.Metadata.MetadataUpdater.IsSupported", scope);
        AssertFalseProperty(properties, "System.Text.Json.JsonSerializer.IsReflectionEnabledByDefault", scope);
    }

    private static void AssertFalseProperty(JsonElement properties, string name, string scope)
    {
        if (!properties.TryGetProperty(name, out var value) ||
            value.ValueKind is not JsonValueKind.False)
        {
            throw new InvalidOperationException(
                $"The {scope} runtime configuration does not explicitly disable '{name}'.");
        }
    }

    private static void AssertExcludedRuntimeTools(string root)
    {
        var invalid = Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories)
            .Where(path =>
            {
                var name = Path.GetFileName(path);
                return name.Equals("createdump.exe", StringComparison.OrdinalIgnoreCase) ||
                       name.Equals("mscordaccore.dll", StringComparison.OrdinalIgnoreCase) ||
                       name.StartsWith("mscordaccore_", StringComparison.OrdinalIgnoreCase) ||
                       name.Equals("mscordbi.dll", StringComparison.OrdinalIgnoreCase) ||
                       name.Equals("Microsoft.DiaSymReader.Native.amd64.dll", StringComparison.OrdinalIgnoreCase) ||
                       name.Equals("Avalonia.DesignerSupport.dll", StringComparison.OrdinalIgnoreCase) ||
                       name.Equals("Avalonia.Remote.Protocol.dll", StringComparison.OrdinalIgnoreCase);
            })
            .ToArray();
        if (invalid.Length != 0)
            throw new InvalidOperationException($"Unused debugger/designer assets remain: {string.Join(", ", invalid)}");
    }

    private static void AssertBinaryContains(string path, string marker)
    {
        var bytes = File.ReadAllBytes(path);
        var markerBytes = Encoding.UTF8.GetBytes(marker);
        if (bytes.AsSpan().IndexOf(markerBytes) >= 0)
            return;
        throw new InvalidOperationException($"Trim-critical marker '{marker}' was not found in '{path}'.");
    }

    private static void AssertAllDllsOwnedByManifests(
        string root,
        IEnumerable<string> manifestPaths,
        string scope)
    {
        var owned = DependencyManifestAssetInventory.ReadOwnedFileNames(manifestPaths);
        var unowned = Directory.EnumerateFiles(root, "*.dll", SearchOption.AllDirectories)
            .Where(path =>
                !owned.Contains(Path.GetFileName(path)) &&
                !ManifestOwnedDllPruner.IsExplicitlyAllowed(Path.GetFileName(path)))
            .Select(path => Path.GetRelativePath(root, path).Replace(Path.DirectorySeparatorChar, '/'))
            .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
            .ToArray();

        if (unowned.Length != 0)
        {
            throw new InvalidOperationException(
                $"The {scope} contains DLLs that are not owned by its dependency manifest(s): " +
                string.Join(", ", unowned));
        }
    }

    private static void AssertManifestAssetsExist(string productRoot, string manifestPath)
    {
        var filesByName = Directory.EnumerateFiles(productRoot, "*", SearchOption.AllDirectories)
            .Select(Path.GetFileName)
            .Where(name => !string.IsNullOrEmpty(name))
            .Select(name => name!)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        var missing = DependencyManifestAssetInventory.ReadOwnedFileNames([manifestPath])
            .Where(fileName => !filesByName.Contains(fileName))
            .OrderBy(fileName => fileName, StringComparer.OrdinalIgnoreCase)
            .ToArray();

        if (missing.Length != 0)
            throw new InvalidOperationException($"Manifest assets are missing from the final product: {string.Join(", ", missing)}");
    }
}
