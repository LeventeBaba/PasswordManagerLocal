using System.Text;

namespace PasswordManagerLocal.Windows.Packaging;

internal static class BaselineTreeAnalyzer
{
    public static BaselineAnalysis? Analyze(string? path, long? totalBytes)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return totalBytes.HasValue
                ? new BaselineAnalysis(totalBytes, null, null, 0, 0, 0,
                    "Only a supplied total byte count was available; per-process legacy sizes were not provided.")
                : null;
        }

        var fullPath = Path.GetFullPath(path);
        if (!File.Exists(fullPath))
            throw new FileNotFoundException("Baseline publish tree not found.", fullPath);

        var frontendFiles = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var agentFiles = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var inAgent = false;
        foreach (var line in File.ReadLines(fullPath, Encoding.Latin1))
        {
            var trimmed = line.Trim();
            if (trimmed.Length == 0 ||
                trimmed.StartsWith("Folder PATH", StringComparison.OrdinalIgnoreCase) ||
                trimmed.StartsWith("Volume serial", StringComparison.OrdinalIgnoreCase) ||
                trimmed.Equals("C:.", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (trimmed.Contains("AgentRuntime", StringComparison.OrdinalIgnoreCase) &&
                (trimmed.StartsWith("+---", StringComparison.Ordinal) ||
                 trimmed.StartsWith("\\---", StringComparison.Ordinal)))
            {
                inAgent = true;
                continue;
            }

            if (trimmed.StartsWith("+---", StringComparison.Ordinal) ||
                trimmed.StartsWith("\\---", StringComparison.Ordinal))
            {
                continue;
            }

            var name = trimmed.TrimStart('¦', '│', '├', '└', '─', '+', '-', ' ');
            if (name.Length == 0 || name.EndsWith(':'))
                continue;
            (inAgent ? agentFiles : frontendFiles).Add(name);
        }

        var duplicateCandidates = frontendFiles.Intersect(agentFiles, StringComparer.OrdinalIgnoreCase).Count();
        var note = totalBytes.HasValue
            ? "The supplied tree provided filenames but no per-file sizes. The total baseline was supplied separately; frontend, Agent, and duplicate byte totals remain unavailable."
            : "The supplied tree provided filenames but no byte sizes. Exact legacy total, frontend, Agent, and duplicate byte totals cannot be derived from it.";
        return new BaselineAnalysis(
            totalBytes,
            null,
            null,
            frontendFiles.Count,
            agentFiles.Count,
            duplicateCandidates,
            note);
    }
}
