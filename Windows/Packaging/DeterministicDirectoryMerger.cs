using System.Security.Cryptography;

namespace PasswordManagerLocal.Windows.Packaging;

public sealed class DeterministicDirectoryMerger
{
    private static readonly StringComparer WindowsPathComparer = StringComparer.OrdinalIgnoreCase;

    public MergeResult Merge(
        string frontendDirectory,
        string agentDirectory,
        string outputDirectory)
    {
        var frontendRoot = NormalizeDirectory(frontendDirectory, nameof(frontendDirectory));
        var agentRoot = NormalizeDirectory(agentDirectory, nameof(agentDirectory));
        var outputRoot = Path.GetFullPath(outputDirectory);

        EnsureDistinctRoots(frontendRoot, agentRoot, outputRoot);
        RecreateDirectory(outputRoot);

        var frontend = Inventory(frontendRoot, "frontend");
        var agent = Inventory(agentRoot, "Agent");
        var keys = frontend.Keys
            .Concat(agent.Keys)
            .Distinct(WindowsPathComparer)
            .OrderBy(static value => value, WindowsPathComparer)
            .ThenBy(static value => value, StringComparer.Ordinal)
            .ToArray();

        var entries = new List<PackageFileEntry>(keys.Length);
        foreach (var key in keys)
        {
            var hasFrontend = frontend.TryGetValue(key, out var frontendFile);
            var hasAgent = agent.TryGetValue(key, out var agentFile);

            if (hasFrontend && hasAgent)
            {
                if (!string.Equals(
                        frontendFile!.RelativePath,
                        agentFile!.RelativePath,
                        StringComparison.Ordinal))
                {
                    throw CreateCollision(
                        "case-only path collision",
                        frontendFile,
                        agentFile);
                }

                var frontendHash = frontendFile.GetSha256();
                var agentHash = agentFile.GetSha256();
                if (frontendFile.Length != agentFile.Length ||
                    !string.Equals(frontendHash, agentHash, StringComparison.Ordinal))
                {
                    throw CreateCollision(
                        "same-path files have different content",
                        frontendFile,
                        agentFile);
                }

                Copy(frontendFile, outputRoot);
                entries.Add(new PackageFileEntry(
                    frontendFile.RelativePath,
                    PackageFileSource.Both,
                    frontendFile.Length,
                    frontendHash,
                    PackageMergeAction.DeduplicatedIdentical,
                    PackageFileClassifier.Classify(frontendFile.RelativePath)));
                continue;
            }

            var sourceFile = frontendFile ?? agentFile!;
            Copy(sourceFile, outputRoot);
            entries.Add(new PackageFileEntry(
                sourceFile.RelativePath,
                hasFrontend ? PackageFileSource.Frontend : PackageFileSource.Agent,
                sourceFile.Length,
                sourceFile.GetSha256(),
                hasFrontend ? PackageMergeAction.CopiedFrontend : PackageMergeAction.CopiedAgent,
                PackageFileClassifier.Classify(sourceFile.RelativePath)));
        }

        var orderedEntries = entries
            .OrderBy(static entry => entry.RelativePath, WindowsPathComparer)
            .ThenBy(static entry => entry.RelativePath, StringComparer.Ordinal)
            .ToArray();

        var summary = new PackageSummary(
            FrontendStagedFiles: frontend.Count,
            FrontendStagedBytes: frontend.Values.Sum(static file => file.Length),
            AgentStagedFiles: agent.Count,
            AgentStagedBytes: agent.Values.Sum(static file => file.Length),
            SharedIdenticalFiles: orderedEntries.Count(static entry => entry.Source == PackageFileSource.Both),
            SharedIdenticalBytes: orderedEntries.Where(static entry => entry.Source == PackageFileSource.Both).Sum(static entry => entry.Length),
            FrontendOnlyFiles: orderedEntries.Count(static entry => entry.Source == PackageFileSource.Frontend),
            FrontendOnlyBytes: orderedEntries.Where(static entry => entry.Source == PackageFileSource.Frontend).Sum(static entry => entry.Length),
            AgentOnlyFiles: orderedEntries.Count(static entry => entry.Source == PackageFileSource.Agent),
            AgentOnlyBytes: orderedEntries.Where(static entry => entry.Source == PackageFileSource.Agent).Sum(static entry => entry.Length),
            CollisionCount: 0,
            BytesRemovedThroughDeduplication: orderedEntries.Where(static entry => entry.Source == PackageFileSource.Both).Sum(static entry => entry.Length),
            FinalFileCount: orderedEntries.Length,
            FinalSizeBytes: orderedEntries.Sum(static entry => entry.Length));

        VerifyFilesystemMatchesReport(outputRoot, orderedEntries, summary);
        return new MergeResult(orderedEntries, summary);
    }

    private static Dictionary<string, InventoryFile> Inventory(string root, string scope)
    {
        var result = new Dictionary<string, InventoryFile>(WindowsPathComparer);
        var files = Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories)
            .OrderBy(static path => path, StringComparer.Ordinal)
            .ToArray();

        foreach (var absolutePath in files)
        {
            RejectReparsePoint(root, absolutePath);
            var relativePath = NormalizeRelativePath(root, absolutePath);
            var item = new InventoryFile(relativePath, absolutePath, new FileInfo(absolutePath).Length);
            if (result.TryGetValue(relativePath, out var existing))
            {
                throw new PackagingCollisionException(
                    $"The {scope} staging directory contains a Windows path collision. " +
                    $"Relative path A: {existing.RelativePath}; size A: {existing.Length}; SHA-256 A: {existing.GetSha256()}; " +
                    $"path A: {existing.AbsolutePath}; relative path B: {item.RelativePath}; " +
                    $"size B: {item.Length}; SHA-256 B: {item.GetSha256()}; path B: {item.AbsolutePath}.");
            }

            result.Add(relativePath, item);
        }

        if (result.Count == 0)
            throw new InvalidOperationException($"The {scope} staging directory is empty: {root}");

        return result;
    }

    private static string NormalizeDirectory(string path, string parameterName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path, parameterName);
        var fullPath = Path.GetFullPath(path);
        if (!Directory.Exists(fullPath))
            throw new DirectoryNotFoundException($"Staging directory not found: {fullPath}");
        if ((new DirectoryInfo(fullPath).Attributes & FileAttributes.ReparsePoint) != 0)
            throw new InvalidOperationException($"A staging root cannot be a reparse point: {fullPath}");
        return Path.TrimEndingDirectorySeparator(fullPath);
    }

    private static string NormalizeRelativePath(string root, string absolutePath)
    {
        var relative = Path.GetRelativePath(root, absolutePath)
            .Replace(Path.DirectorySeparatorChar, '/')
            .Replace(Path.AltDirectorySeparatorChar, '/');

        if (Path.IsPathRooted(relative) ||
            relative.Equals("..", StringComparison.Ordinal) ||
            relative.StartsWith("../", StringComparison.Ordinal) ||
            relative.Contains("/../", StringComparison.Ordinal) ||
            relative.Contains('\0'))
        {
            throw new InvalidOperationException(
                $"A staged file resolves outside its staging root: {absolutePath}");
        }

        return relative;
    }

    private static void EnsureDistinctRoots(string frontendRoot, string agentRoot, string outputRoot)
    {
        if (PathsOverlap(frontendRoot, agentRoot) ||
            PathsOverlap(frontendRoot, outputRoot) ||
            PathsOverlap(agentRoot, outputRoot))
        {
            throw new InvalidOperationException(
                "Frontend staging, Agent staging, and final output directories must be distinct and non-nested.");
        }
    }

    private static bool PathsOverlap(string left, string right)
    {
        var leftWithSeparator = Path.TrimEndingDirectorySeparator(Path.GetFullPath(left)) + Path.DirectorySeparatorChar;
        var rightWithSeparator = Path.TrimEndingDirectorySeparator(Path.GetFullPath(right)) + Path.DirectorySeparatorChar;
        return leftWithSeparator.StartsWith(rightWithSeparator, StringComparison.OrdinalIgnoreCase) ||
               rightWithSeparator.StartsWith(leftWithSeparator, StringComparison.OrdinalIgnoreCase);
    }

    private static void RecreateDirectory(string path)
    {
        if (Directory.Exists(path))
        {
            if ((new DirectoryInfo(path).Attributes & FileAttributes.ReparsePoint) != 0)
                throw new InvalidOperationException($"The final output directory cannot be a reparse point: {path}");
            Directory.Delete(path, recursive: true);
        }
        Directory.CreateDirectory(path);
    }

    private static void Copy(InventoryFile source, string outputRoot)
    {
        var relativeForPlatform = source.RelativePath.Replace('/', Path.DirectorySeparatorChar);
        var destination = Path.GetFullPath(Path.Combine(outputRoot, relativeForPlatform));
        var outputPrefix = Path.TrimEndingDirectorySeparator(outputRoot) + Path.DirectorySeparatorChar;
        if (!destination.StartsWith(outputPrefix, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException($"Destination escaped the final output directory: {destination}");

        Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
        File.Copy(source.AbsolutePath, destination, overwrite: false);
    }

    private static PackagingCollisionException CreateCollision(
        string reason,
        InventoryFile frontend,
        InventoryFile agent)
    {
        return new PackagingCollisionException(
            $"Windows packaging failed because {reason}. " +
            $"Relative path: {frontend.RelativePath}; " +
            $"frontend size: {frontend.Length}; frontend SHA-256: {frontend.GetSha256()}; " +
            $"Agent size: {agent.Length}; Agent SHA-256: {agent.GetSha256()}; " +
            $"frontend source: {frontend.AbsolutePath}; Agent source: {agent.AbsolutePath}.");
    }

    private static void RejectReparsePoint(string root, string file)
    {
        var normalizedRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));
        var fileInfo = new FileInfo(file);

        if ((fileInfo.Attributes & FileAttributes.ReparsePoint) != 0)
            throw new InvalidOperationException($"Reparse points are not allowed in packaging staging: {fileInfo.FullName}");

        DirectoryInfo? current = fileInfo.Directory;
        while (current is not null &&
               !string.Equals(
                   Path.TrimEndingDirectorySeparator(current.FullName),
                   normalizedRoot,
                   StringComparison.OrdinalIgnoreCase))
        {
            if ((current.Attributes & FileAttributes.ReparsePoint) != 0)
                throw new InvalidOperationException($"Reparse points are not allowed in packaging staging: {current.FullName}");

            current = current.Parent;
        }
    }

    private static void VerifyFilesystemMatchesReport(
        string outputRoot,
        IReadOnlyList<PackageFileEntry> entries,
        PackageSummary summary)
    {
        var outputFiles = Directory.EnumerateFiles(outputRoot, "*", SearchOption.AllDirectories).ToArray();
        var outputBytes = outputFiles.Sum(static path => new FileInfo(path).Length);
        if (outputFiles.Length != summary.FinalFileCount || outputBytes != summary.FinalSizeBytes)
        {
            throw new InvalidOperationException(
                $"Final filesystem totals do not match the merge report. " +
                $"Reported files/bytes: {summary.FinalFileCount}/{summary.FinalSizeBytes}; " +
                $"actual files/bytes: {outputFiles.Length}/{outputBytes}.");
        }

        foreach (var entry in entries)
        {
            var path = Path.Combine(outputRoot, entry.RelativePath.Replace('/', Path.DirectorySeparatorChar));
            if (!File.Exists(path))
                throw new InvalidOperationException($"Merged file is missing: {entry.RelativePath}");
            var length = new FileInfo(path).Length;
            var hash = ComputeSha256(path);
            if (length != entry.Length || !string.Equals(hash, entry.Sha256, StringComparison.Ordinal))
                throw new InvalidOperationException($"Merged file changed after copy: {entry.RelativePath}");
        }
    }

    internal static string ComputeSha256(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        return Convert.ToHexString(SHA256.HashData(stream));
    }

    private sealed class InventoryFile
    {
        private string? _sha256;

        public InventoryFile(string relativePath, string absolutePath, long length)
        {
            RelativePath = relativePath;
            AbsolutePath = absolutePath;
            Length = length;
        }

        public string RelativePath { get; }
        public string AbsolutePath { get; }
        public long Length { get; }

        public string GetSha256() => _sha256 ??= ComputeSha256(AbsolutePath);
    }
}
