using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace PasswordManagerLocal.Windows.Packaging;

internal static class PackageReportWriter
{
    public static void Write(PackageReport report, string reportDirectory)
    {
        var root = Path.GetFullPath(reportDirectory);
        if (Directory.Exists(root))
            Directory.Delete(root, recursive: true);
        Directory.CreateDirectory(root);

        var jsonOptions = new JsonSerializerOptions
        {
            WriteIndented = true,
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase
        };
        jsonOptions.Converters.Add(new JsonStringEnumConverter(JsonNamingPolicy.CamelCase));
        var json = JsonSerializer.Serialize(report, jsonOptions).Replace("\r\n", "\n", StringComparison.Ordinal);
        File.WriteAllText(Path.Combine(root, "packaging-report.json"), json + "\n", new UTF8Encoding(false));
        File.WriteAllText(Path.Combine(root, "packaging-report.txt"), CreateTextReport(report), new UTF8Encoding(false));
    }

    private static string CreateTextReport(PackageReport report)
    {
        var summary = report.Summary;
        var builder = new StringBuilder();
        builder.AppendLine("PasswordManagerLocal Windows packaging report");
        builder.AppendLine("============================================");
        builder.AppendLine($"Target framework: {report.TargetFramework}");
        builder.AppendLine($"Runtime identifier: {report.RuntimeIdentifier}");
        builder.AppendLine();
        builder.AppendLine($"Frontend staged files: {summary.FrontendStagedFiles}");
        builder.AppendLine($"Frontend staged size: {FormatBytes(summary.FrontendStagedBytes)} ({summary.FrontendStagedBytes} bytes)");
        builder.AppendLine($"Agent staged files: {summary.AgentStagedFiles}");
        builder.AppendLine($"Agent staged size: {FormatBytes(summary.AgentStagedBytes)} ({summary.AgentStagedBytes} bytes)");
        builder.AppendLine($"Shared identical files: {summary.SharedIdenticalFiles}");
        builder.AppendLine($"Shared identical size: {FormatBytes(summary.SharedIdenticalBytes)} ({summary.SharedIdenticalBytes} bytes)");
        builder.AppendLine($"Frontend-only files: {summary.FrontendOnlyFiles}");
        builder.AppendLine($"Frontend-only size: {FormatBytes(summary.FrontendOnlyBytes)} ({summary.FrontendOnlyBytes} bytes)");
        builder.AppendLine($"Agent-only files: {summary.AgentOnlyFiles}");
        builder.AppendLine($"Agent-only size: {FormatBytes(summary.AgentOnlyBytes)} ({summary.AgentOnlyBytes} bytes)");
        builder.AppendLine($"Collision count: {summary.CollisionCount}");
        builder.AppendLine($"Bytes removed through deduplication: {FormatBytes(summary.BytesRemovedThroughDeduplication)} ({summary.BytesRemovedThroughDeduplication} bytes)");
        builder.AppendLine($"Final file count: {summary.FinalFileCount}");
        builder.AppendLine($"Final size: {FormatBytes(summary.FinalSizeBytes)} ({summary.FinalSizeBytes} bytes)");

        if (report.Baseline is not null)
        {
            builder.AppendLine();
            builder.AppendLine("Supplied baseline");
            builder.AppendLine("-----------------");
            builder.AppendLine($"Total size: {FormatNullable(report.Baseline.TotalProductBytes)}");
            builder.AppendLine($"Frontend size: {FormatNullable(report.Baseline.FrontendBytes)}");
            builder.AppendLine($"Agent size: {FormatNullable(report.Baseline.AgentBytes)}");
            builder.AppendLine($"Frontend files: {report.Baseline.FrontendFileCount}");
            builder.AppendLine($"Agent files: {report.Baseline.AgentFileCount}");
            builder.AppendLine($"Same-name duplicate candidates: {report.Baseline.DuplicateNameCandidateCount}");
            builder.AppendLine($"Note: {report.Baseline.Note}");
            if (report.Baseline.TotalProductBytes is long baselineBytes)
            {
                var saved = baselineBytes - summary.FinalSizeBytes;
                var percentage = baselineBytes == 0 ? 0 : saved * 100d / baselineBytes;
                builder.AppendLine($"Absolute bytes saved: {FormatBytes(saved)} ({saved} bytes)");
                builder.AppendLine($"Percentage saved: {percentage:F2}%");
            }
        }

        AppendLargest(builder, "Largest remaining files", report.Files, static _ => true);
        AppendLargest(builder, "Largest shared files", report.Files, static entry => entry.Source == PackageFileSource.Both);
        AppendLargest(builder, "Largest frontend-only files", report.Files, static entry => entry.Source == PackageFileSource.Frontend);
        AppendLargest(builder, "Largest Agent-only files", report.Files, static entry => entry.Source == PackageFileSource.Agent);
        return builder.ToString().Replace("\r\n", "\n", StringComparison.Ordinal);
    }

    private static void AppendLargest(
        StringBuilder builder,
        string title,
        IEnumerable<PackageFileEntry> entries,
        Func<PackageFileEntry, bool> predicate)
    {
        builder.AppendLine();
        builder.AppendLine(title);
        builder.AppendLine(new string('-', title.Length));
        foreach (var entry in entries.Where(predicate)
                     .OrderByDescending(static entry => entry.Length)
                     .ThenBy(static entry => entry.RelativePath, StringComparer.OrdinalIgnoreCase)
                     .Take(15))
        {
            builder.AppendLine($"{entry.Length,12}  {FormatBytes(entry.Length),10}  {entry.RelativePath}");
        }
    }

    private static string FormatNullable(long? value) =>
        value.HasValue ? $"{FormatBytes(value.Value)} ({value.Value} bytes)" : "not available";

    private static string FormatBytes(long value)
    {
        var sign = value < 0 ? "-" : string.Empty;
        var absolute = Math.Abs((double)value);
        if (absolute >= 1024d * 1024d * 1024d)
            return $"{sign}{absolute / (1024d * 1024d * 1024d):F2} GiB";
        if (absolute >= 1024d * 1024d)
            return $"{sign}{absolute / (1024d * 1024d):F2} MiB";
        if (absolute >= 1024d)
            return $"{sign}{absolute / 1024d:F2} KiB";
        return $"{value} B";
    }
}
