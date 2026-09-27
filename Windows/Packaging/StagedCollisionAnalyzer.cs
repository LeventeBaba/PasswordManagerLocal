using System.Security.Cryptography;
using System.Text.Json;

namespace PasswordManagerLocal.Windows.Packaging;

public sealed class StagedCollisionAnalyzer
{
    private static readonly StringComparer WindowsPathComparer = StringComparer.OrdinalIgnoreCase;

    public StagedCollisionAnalysis Analyze(
        string frontendDirectory,
        string agentDirectory)
    {
        var frontendRoot = NormalizeDirectory(frontendDirectory, nameof(frontendDirectory));
        var agentRoot = NormalizeDirectory(agentDirectory, nameof(agentDirectory));
        var frontend = Inventory(frontendRoot, "frontend");
        var agent = Inventory(agentRoot, "Agent");
        var collisions = new List<StagedCollision>();

        foreach (var relativePath in frontend.Keys
                     .Intersect(agent.Keys, WindowsPathComparer)
                     .OrderBy(static value => value, StringComparer.Ordinal))
        {
            var frontendFile = frontend[relativePath];
            var agentFile = agent[relativePath];
            if (frontendFile.Length == agentFile.Length &&
                frontendFile.Sha256.Equals(agentFile.Sha256, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var assemblyName = GetCopyUsedAssemblyName(relativePath);
            collisions.Add(new StagedCollision(
                RelativePath: relativePath,
                FrontendLength: frontendFile.Length,
                FrontendSha256: frontendFile.Sha256,
                AgentLength: agentFile.Length,
                AgentSha256: agentFile.Sha256,
                CanResolveWithCopyUsed: assemblyName is not null,
                AssemblyName: assemblyName));
        }

        var copyUsedAssemblies = collisions
            .Where(static collision => collision.CanResolveWithCopyUsed)
            .Select(static collision => collision.AssemblyName!)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(static value => value, StringComparer.Ordinal)
            .ToArray();

        return new StagedCollisionAnalysis(
            SchemaVersion: "1.0",
            Collisions: collisions,
            CopyUsedAssemblies: copyUsedAssemblies);
    }

    public StagedCollisionAnalysis AnalyzeAndWrite(
        string frontendDirectory,
        string agentDirectory,
        string reportPath,
        string assemblyListPath)
    {
        var analysis = Analyze(frontendDirectory, agentDirectory);
        WriteJson(reportPath, analysis);
        WriteAssemblyList(assemblyListPath, analysis.CopyUsedAssemblies);

        var unsupported = analysis.Collisions
            .Where(static collision => !collision.CanResolveWithCopyUsed)
            .ToArray();
        if (unsupported.Length != 0)
        {
            throw new PackagingCollisionException(
                "Windows packaging found collisions that cannot be made shareable with a narrow ILLink copyused action: " +
                string.Join("; ", unsupported.Select(static collision =>
                    $"{collision.RelativePath} (frontend {collision.FrontendLength}/{collision.FrontendSha256}, " +
                    $"Agent {collision.AgentLength}/{collision.AgentSha256})")));
        }

        return analysis;
    }

    private static string? GetCopyUsedAssemblyName(string relativePath)
    {
        if (!Path.GetExtension(relativePath).Equals(".dll", StringComparison.OrdinalIgnoreCase))
            return null;

        var fileName = Path.GetFileNameWithoutExtension(relativePath);
        if (fileName.StartsWith("System.", StringComparison.Ordinal) ||
            fileName.StartsWith("Microsoft.Win32.", StringComparison.Ordinal) ||
            fileName.Equals("System", StringComparison.Ordinal) ||
            fileName.Equals("Microsoft.CSharp", StringComparison.Ordinal) ||
            fileName.Equals("Microsoft.VisualBasic", StringComparison.Ordinal) ||
            fileName.Equals("Microsoft.VisualBasic.Core", StringComparison.Ordinal) ||
            fileName.Equals("netstandard", StringComparison.Ordinal) ||
            fileName.Equals("mscorlib", StringComparison.Ordinal) ||
            fileName.Equals("PasswordManagerLocal.Common.Contracts", StringComparison.Ordinal) ||
            fileName.Equals("PasswordManagerLocal.Common.Preferences", StringComparison.Ordinal) ||
            fileName.Equals("PasswordManagerLocal.Windows.EndpointRpc.Contracts", StringComparison.Ordinal) ||
            fileName.Equals("PasswordManagerLocal.Windows.Ipc", StringComparison.Ordinal))
        {
            return fileName;
        }

        return null;
    }

    private static Dictionary<string, InventoryFile> Inventory(string root, string scope)
    {
        var files = new Dictionary<string, InventoryFile>(WindowsPathComparer);
        foreach (var path in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories)
                     .OrderBy(static value => value, StringComparer.Ordinal))
        {
            RejectReparsePoint(root, path);
            var relativePath = Path.GetRelativePath(root, path)
                .Replace(Path.DirectorySeparatorChar, '/');
            if (relativePath.StartsWith("../", StringComparison.Ordinal) ||
                Path.IsPathRooted(relativePath))
            {
                throw new InvalidOperationException(
                    $"The {scope} staging inventory escaped its root: {path}");
            }

            if (files.TryGetValue(relativePath, out var previous))
            {
                throw new PackagingCollisionException(
                    $"Windows case-only path collision in {scope} staging: " +
                    $"'{previous.RelativePath}' and '{relativePath}'.");
            }

            var bytes = File.ReadAllBytes(path);
            files.Add(relativePath, new InventoryFile(
                RelativePath: relativePath,
                Length: bytes.LongLength,
                Sha256: Convert.ToHexString(SHA256.HashData(bytes))));
        }

        return files;
    }


    private static void RejectReparsePoint(string root, string file)
    {
        var normalizedRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));
        var fileInfo = new FileInfo(file);
        if ((fileInfo.Attributes & FileAttributes.ReparsePoint) != 0)
        {
            throw new InvalidOperationException(
                $"Reparse points are not allowed in packaging staging: {fileInfo.FullName}");
        }

        DirectoryInfo? current = fileInfo.Directory;
        while (current is not null &&
               !string.Equals(
                   Path.TrimEndingDirectorySeparator(current.FullName),
                   normalizedRoot,
                   StringComparison.OrdinalIgnoreCase))
        {
            if ((current.Attributes & FileAttributes.ReparsePoint) != 0)
            {
                throw new InvalidOperationException(
                    $"Reparse points are not allowed in packaging staging: {current.FullName}");
            }

            current = current.Parent;
        }
    }

    private static string NormalizeDirectory(string path, string parameterName)
    {
        if (string.IsNullOrWhiteSpace(path))
            throw new ArgumentException("A staging directory is required.", parameterName);
        var fullPath = Path.GetFullPath(path);
        if (!Directory.Exists(fullPath))
            throw new DirectoryNotFoundException($"Staging directory does not exist: {fullPath}");
        return Path.TrimEndingDirectorySeparator(fullPath);
    }

    private static void WriteJson(string path, StagedCollisionAnalysis analysis)
    {
        var fullPath = Path.GetFullPath(path);
        Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
        var options = new JsonSerializerOptions { WriteIndented = true };
        File.WriteAllText(fullPath, JsonSerializer.Serialize(analysis, options));
    }

    private static void WriteAssemblyList(string path, IReadOnlyList<string> assemblies)
    {
        var fullPath = Path.GetFullPath(path);
        Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
        File.WriteAllLines(fullPath, assemblies);
    }

    private sealed record InventoryFile(string RelativePath, long Length, string Sha256);
}
