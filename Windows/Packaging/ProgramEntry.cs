using PasswordManagerLocal.Windows.Packaging;

return ProgramEntry.Run(args);

internal static class ProgramEntry
{
    public static int Run(string[] args)
    {
        try
        {
            if (args.Length == 0)
            {
                PrintUsage();
                return 2;
            }

            var command = args[0];
            var values = ParseArguments(args.Skip(1).ToArray());
            if (command.Equals("merge", StringComparison.OrdinalIgnoreCase))
                return RunMerge(values);
            if (command.Equals("analyze-collisions", StringComparison.OrdinalIgnoreCase))
                return RunCollisionAnalysis(values);
            if (command.Equals("prune-unowned-dlls", StringComparison.OrdinalIgnoreCase))
                return RunPruneUnownedDlls(values);

            PrintUsage();
            return 2;
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine(exception.Message);
            return 1;
        }
    }

    private static int RunMerge(IReadOnlyDictionary<string, string> values)
    {
        var options = new PackageOptions(
            FrontendDirectory: Required(values, "frontend"),
            AgentDirectory: Required(values, "agent"),
            OutputDirectory: Required(values, "output"),
            ReportDirectory: Required(values, "report"),
            BaselineTreePath: Optional(values, "baseline-tree"),
            BaselineTotalBytes: ParseOptionalLong(values, "baseline-total-bytes"),
            RuntimeIdentifier: Optional(values, "runtime-identifier") ?? "win-x64",
            TargetFramework: Optional(values, "target-framework") ?? "net10.0-windows");

        var report = new WindowsProductPackager().Package(options);
        Console.WriteLine($"Windows product package created: {Path.GetFullPath(options.OutputDirectory)}");
        Console.WriteLine($"Final files: {report.Summary.FinalFileCount}");
        Console.WriteLine($"Final bytes: {report.Summary.FinalSizeBytes}");
        Console.WriteLine($"Deduplicated files: {report.Summary.SharedIdenticalFiles}");
        Console.WriteLine($"Deduplicated bytes: {report.Summary.BytesRemovedThroughDeduplication}");
        return 0;
    }

    private static int RunCollisionAnalysis(IReadOnlyDictionary<string, string> values)
    {
        var frontendDirectory = Required(values, "frontend");
        var agentDirectory = Required(values, "agent");
        var reportPath = Required(values, "report");
        var assemblyListPath = Required(values, "assembly-list");

        var validator = new WindowsProductValidator();
        validator.ValidateFrontendStage(frontendDirectory);
        validator.ValidateAgentStage(agentDirectory);

        var analysis = new StagedCollisionAnalyzer().AnalyzeAndWrite(
            frontendDirectory,
            agentDirectory,
            reportPath,
            assemblyListPath);
        Console.WriteLine($"Staged same-path collisions: {analysis.Collisions.Count}");
        Console.WriteLine($"Assemblies requiring copyused: {analysis.CopyUsedAssemblies.Count}");
        return 0;
    }

    private static int RunPruneUnownedDlls(IReadOnlyDictionary<string, string> values)
    {
        var directory = Required(values, "directory");
        var manifest = Required(values, "manifest");
        var report = Optional(values, "report");

        var removed = new ManifestOwnedDllPruner().Prune(directory, [manifest], report);
        Console.WriteLine($"Removed unowned DLLs: {removed.Count}");
        foreach (var relativePath in removed)
            Console.WriteLine($"  {relativePath}");
        return 0;
    }

    private static Dictionary<string, string> ParseArguments(string[] args)
    {
        var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        for (var index = 0; index < args.Length; index += 2)
        {
            var name = args[index];
            if (!name.StartsWith("--", StringComparison.Ordinal) || index + 1 >= args.Length)
                throw new ArgumentException($"Invalid packaging argument near '{name}'.");
            var key = name[2..];
            if (!values.TryAdd(key, args[index + 1]))
                throw new ArgumentException($"Duplicate packaging argument: --{key}");
        }
        return values;
    }

    private static string Required(IReadOnlyDictionary<string, string> values, string name) =>
        values.TryGetValue(name, out var value) && !string.IsNullOrWhiteSpace(value)
            ? value
            : throw new ArgumentException($"Missing required argument: --{name}");

    private static string? Optional(IReadOnlyDictionary<string, string> values, string name) =>
        values.TryGetValue(name, out var value) && !string.IsNullOrWhiteSpace(value) ? value : null;

    private static long? ParseOptionalLong(IReadOnlyDictionary<string, string> values, string name)
    {
        var value = Optional(values, name);
        if (value is null)
            return null;
        if (!long.TryParse(value, out var parsed) || parsed < 0)
            throw new ArgumentException($"--{name} must be a non-negative integer byte count.");
        return parsed;
    }

    private static void PrintUsage()
    {
        Console.Error.WriteLine(
            "Usage:\n" +
            "  merge --frontend <dir> --agent <dir> --output <dir> --report <dir> " +
            "[--baseline-tree <file>] [--baseline-total-bytes <bytes>] " +
            "[--runtime-identifier win-x64] [--target-framework net10.0-windows]\n" +
            "  analyze-collisions --frontend <dir> --agent <dir> " +
            "--report <json-file> --assembly-list <text-file>\n" +
            "  prune-unowned-dlls --directory <dir> --manifest <deps-json> " +
            "[--report <text-file>]");
    }
}
