using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace PasswordManagerLocal.Windows.Packaging;

public sealed class SignedProductReportRefresher
{
    public PackageReport Refresh(string productDirectory, string reportDirectory)
    {
        var root = Path.GetFullPath(productDirectory);
        var options = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };
        options.Converters.Add(new JsonStringEnumConverter(JsonNamingPolicy.CamelCase));
        var original = JsonSerializer.Deserialize<PackageReport>(
            File.ReadAllText(Path.Combine(reportDirectory, "packaging-report.json")), options)
            ?? throw new InvalidOperationException("The packaging report is empty.");

        var report = RefreshInventory(root, original);
        new WindowsProductValidator().ValidateFinalProduct(root, report);
        PackageReportWriter.Write(report, reportDirectory, clearDirectory: false);
        return report;
    }

    internal static PackageReport RefreshInventory(string productDirectory, PackageReport original)
    {
        var root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(productDirectory));

        // Signing may change file bytes and lengths, but never the product inventory.
        // Keep stage and deduplication measurements as their pre-signing values.
        var prefix = root + Path.DirectorySeparatorChar;
        var entries = original.Files.Select(entry =>
        {
            var path = Path.GetFullPath(Path.Combine(root, entry.RelativePath));
            if (!path.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("A report path escapes the product directory.");
            using var stream = File.OpenRead(path);
            return entry with { Length = stream.Length, Sha256 = Convert.ToHexString(SHA256.HashData(stream)) };
        }).ToArray();
        var actual = Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories)
            .Select(path => Path.GetRelativePath(root, path).Replace('\\', '/'))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (entries.Length != actual.Count || !actual.SetEquals(entries.Select(entry => entry.RelativePath.Replace('\\', '/'))))
            throw new InvalidOperationException("The signed product inventory differs from the merge report.");

        var report = original with
        {
            Summary = original.Summary with
            {
                FinalFileCount = entries.Length,
                FinalSizeBytes = entries.Sum(entry => entry.Length)
            },
            Files = entries
        };
        return report;
    }
}
