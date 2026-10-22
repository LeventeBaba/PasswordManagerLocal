using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Xml.Linq;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using PasswordManagerLocal.Common.Frontend.Information;

namespace PasswordManagerLocal.Common.Tests.Frontend.Information;

[TestClass]
public sealed class ThirdPartyNoticeCatalogTests
{
    [TestMethod]
    public void BundledCatalogIsReviewedCompleteAndOfflineReadable()
    {
        var catalog = ThirdPartyNoticeCatalogLoader.Load();

        Assert.AreEqual(1, catalog.SchemaVersion);
        Assert.IsTrue(catalog.ReviewedDate <= DateOnly.FromDateTime(DateTime.UtcNow));
        Assert.IsTrue(catalog.Notices.Count >= 10);
        Assert.AreEqual(
            catalog.Notices.Count,
            catalog.Notices.Select(notice => notice.Id).Distinct(StringComparer.Ordinal).Count());
        foreach (var notice in catalog.Notices)
        {
            Assert.IsFalse(string.IsNullOrWhiteSpace(notice.Name), notice.Id);
            Assert.IsFalse(string.IsNullOrWhiteSpace(notice.Version), notice.Id);
            Assert.IsFalse(string.IsNullOrWhiteSpace(notice.LicenseExpression), notice.Id);
            Assert.IsFalse(string.IsNullOrWhiteSpace(notice.CopyrightNotice), notice.Id);
            Assert.IsTrue(notice.SourceUri.Scheme.Equals(
                Uri.UriSchemeHttps,
                StringComparison.OrdinalIgnoreCase), notice.Id);
            Assert.IsTrue(notice.LicenseText.Length > 200, notice.Id);
            Assert.AreNotEqual(ThirdPartyPlatform.None, notice.Platforms, notice.Id);
        }
    }

    [TestMethod]
    public void ManifestCoversProductionPackageReferencesAndLockedRuntimePackages()
    {
        var root = GetRepositoryRoot();
        var catalog = ThirdPartyNoticeCatalogLoader.Load();
        var coveredPackages = catalog.Notices
            .SelectMany(notice => notice.Packages)
            .Select(package => package.Name)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var developmentOnly = ReadDevelopmentOnlyPackages(root);

        foreach (var projectPath in Directory.EnumerateFiles(root, "*.csproj", SearchOption.AllDirectories)
                     .Where(IsProductionProject))
        {
            var project = XDocument.Load(projectPath);
            foreach (var packageName in project.Descendants("PackageReference")
                         .Select(reference => (string?)reference.Attribute("Include"))
                         .Where(name => !string.IsNullOrWhiteSpace(name)))
            {
                Assert.IsTrue(
                    coveredPackages.Contains(packageName!) || developmentOnly.Contains(packageName!),
                    $"Production package reference '{packageName}' has no reviewed notice or explicit development-only classification ({projectPath}).");
            }
        }

        foreach (var lockPath in Directory.EnumerateFiles(root, "packages.lock.json", SearchOption.AllDirectories)
                     .Where(path => !IsTestPath(path)))
        {
            using var document = JsonDocument.Parse(File.ReadAllText(lockPath));
            foreach (var framework in document.RootElement.GetProperty("dependencies").EnumerateObject())
            {
                foreach (var dependency in framework.Value.EnumerateObject())
                {
                    if (dependency.Value.GetProperty("type").GetString() == "Project")
                        continue;
                    Assert.IsTrue(
                        coveredPackages.Contains(dependency.Name) || developmentOnly.Contains(dependency.Name),
                        $"Locked package '{dependency.Name}' has no reviewed notice or explicit development-only classification ({lockPath}).");
                }
            }
        }
    }

    [TestMethod]
    public void ConfiguredDirectProductionVersionsMatchManifest()
    {
        var root = GetRepositoryRoot();
        var catalog = ThirdPartyNoticeCatalogLoader.Load();
        var reviewedVersions = catalog.Notices
            .SelectMany(notice => notice.Packages)
            .GroupBy(package => package.Name, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(
                group => group.Key,
                group => group.Select(package => package.Version)
                    .ToHashSet(StringComparer.OrdinalIgnoreCase),
                StringComparer.OrdinalIgnoreCase);
        var developmentOnly = ReadDevelopmentOnlyPackages(root);
        var centralPackages = XDocument.Load(Path.Combine(root, "Directory.Packages.props"))
            .Descendants("PackageVersion")
            .ToDictionary(
                item => (string)item.Attribute("Include")!,
                item => (string)item.Attribute("Version")!,
                StringComparer.OrdinalIgnoreCase);

        foreach (var projectPath in Directory.EnumerateFiles(root, "*.csproj", SearchOption.AllDirectories)
                     .Where(IsProductionProject))
        {
            var project = XDocument.Load(projectPath);
            foreach (var reference in project.Descendants("PackageReference"))
            {
                var packageName = (string?)reference.Attribute("Include");
                if (string.IsNullOrWhiteSpace(packageName) || developmentOnly.Contains(packageName))
                    continue;
                var version = (string?)reference.Attribute("Version") ?? centralPackages[packageName];
                Assert.IsTrue(
                    reviewedVersions.TryGetValue(packageName, out var versions) && versions.Contains(version),
                    $"The reviewed notice version for '{packageName}' does not match configured version {version}.");
            }
        }
    }

    private static HashSet<string> ReadDevelopmentOnlyPackages(string root)
    {
        var path = Path.Combine(
            root,
            "Common",
            "Frontend",
            "Assets",
            "Legal",
            "development-only-dependencies.json");
        using var document = JsonDocument.Parse(File.ReadAllText(path));
        return document.RootElement.GetProperty("entries")
            .EnumerateArray()
            .Select(entry => entry.GetProperty("name").GetString()!)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
    }

    private static bool IsProductionProject(string path) =>
        !IsTestPath(path) &&
        !path.Contains($"{Path.DirectorySeparatorChar}Packaging{Path.DirectorySeparatorChar}",
            StringComparison.OrdinalIgnoreCase);

    private static bool IsTestPath(string path) =>
        path.Contains($"{Path.DirectorySeparatorChar}Tests{Path.DirectorySeparatorChar}",
            StringComparison.OrdinalIgnoreCase) ||
        path.Contains($"{Path.DirectorySeparatorChar}Tests.", StringComparison.OrdinalIgnoreCase);

    private static string GetRepositoryRoot([CallerFilePath] string sourcePath = "") =>
        Path.GetFullPath(Path.Combine(Path.GetDirectoryName(sourcePath)!, "..", "..", "..", ".."));
}
