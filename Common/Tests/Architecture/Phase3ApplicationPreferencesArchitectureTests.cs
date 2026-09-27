using Microsoft.VisualStudio.TestTools.UnitTesting;
using PasswordManagerLocal.Common.Contracts.Constants;
using PasswordManagerLocal.Common.Contracts.Preferences;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Xml.Linq;

namespace PasswordManagerLocal.Common.Tests.Architecture;

[TestClass]
public sealed class Phase3ApplicationPreferencesArchitectureTests
{
    [TestMethod]
    public void AuthoritativeContractContainsOnlyVersionLanguageAndTheme()
    {
        var properties = typeof(ApplicationPreferences)
            .GetProperties(BindingFlags.Instance | BindingFlags.Public)
            .Select(property => property.Name)
            .Order(StringComparer.Ordinal)
            .ToArray();

        CollectionAssert.AreEqual(
            new[] { "Language", "SchemaVersion", "Theme" },
            properties);
    }


    [TestMethod]
    public void ExactlyOneAuthoritativeApplicationPreferencesContractIsDeclared()
    {
        var declarations = Directory.EnumerateFiles(
                GetRepositoryRoot(),
                "*.cs",
                SearchOption.AllDirectories)
            .Where(path => !path.Contains(".Test", StringComparison.Ordinal))
            .Where(path => !path.Contains(
                $"{Path.DirectorySeparatorChar}Tests.",
                StringComparison.Ordinal))
            .Where(path => !path.Contains(
                $"{Path.DirectorySeparatorChar}Tests{Path.DirectorySeparatorChar}",
                StringComparison.Ordinal))
            .Where(path => !path.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
            .Select(File.ReadAllText)
            .Count(source => source.Contains(
                "public sealed record ApplicationPreferences",
                StringComparison.Ordinal));

        Assert.AreEqual(1, declarations);
    }

    [TestMethod]
    public void PreferencesInfrastructureReferencesOnlyContractsProject()
    {
        var project = XDocument.Load(Path.Combine(
            GetRepositoryRoot(),
            "Common",
            "Preferences",
            "PasswordManagerLocal.Common.Preferences.csproj"));
        var references = project.Descendants("ProjectReference")
            .Select(reference => reference.Attribute("Include")?.Value ?? string.Empty)
            .ToArray();

        CollectionAssert.AreEqual(
            new[] { @"..\Contracts\PasswordManagerLocal.Common.Contracts.csproj" },
            references);
    }


    [TestMethod]
    public void ContractsPreferenceBoundaryRemainsPlatformNeutral()
    {
        var root = GetRepositoryRoot();
        var contractsProjectPath = Path.Combine(
            root,
            "Common",
            "Contracts",
            "PasswordManagerLocal.Common.Contracts.csproj");
        var project = XDocument.Load(contractsProjectPath);
        Assert.AreEqual(0, project.Descendants("ProjectReference").Count());
        Assert.AreEqual(0, project.Descendants("PackageReference").Count());

        var preferenceSources = Directory.EnumerateFiles(
                Path.Combine(root, "Common", "Contracts", "Preferences"),
                "*.cs",
                SearchOption.TopDirectoryOnly)
            .Select(File.ReadAllText);
        var combined = string.Join('\n', preferenceSources);
        foreach (var forbidden in new[]
        {
            "Avalonia",
            "System.Windows.Forms",
            "Microsoft.EntityFrameworkCore",
            "ReactiveUI",
            "using Android.",
            "System.IO",
            "JsonSerializer",
            "FileStream"
        })
        {
            Assert.IsFalse(combined.Contains(forbidden, StringComparison.Ordinal), forbidden);
        }
    }

    [TestMethod]
    public void DefaultsAreNotRedefinedByApplicationConsumers()
    {
        var root = GetRepositoryRoot();
        var consumerRoots = new[]
        {
            Path.Combine(root, "Common", "Frontend"),
            Path.Combine(root, "Windows", "Agent"),
            Path.Combine(root, "Android", "Frontend")
        };

        var combined = string.Join(
            '\n',
            consumerRoots.SelectMany(directory => Directory.EnumerateFiles(
                    directory,
                    "*.cs",
                    SearchOption.AllDirectories))
                .Where(path => !path.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
                .Select(File.ReadAllText));

        Assert.IsFalse(combined.Contains("new ApplicationPreferences", StringComparison.Ordinal));
        Assert.IsFalse(combined.Contains("DetectDefaultLanguage", StringComparison.Ordinal));
    }

    [TestMethod]
    public void FrontendApplicationHasPublicParameterlessConstructorForXamlLoader()
    {
        var constructor = typeof(PasswordManagerLocal.Common.Frontend.App).GetConstructor(Type.EmptyTypes);

        Assert.IsNotNull(constructor);
    }

    [TestMethod]
    public void ObsoleteFrontendPreferenceModelsAndReadersAreRemoved()
    {
        var services = Path.Combine(
            GetRepositoryRoot(),
            "Common",
            "Frontend",
            "Services");
        foreach (var fileName in new[]
        {
            "AppConfiguration.cs",
            "AppConfigurationJsonContext.cs",
            "AppConfigurationManager.cs"
        })
        {
            Assert.IsFalse(File.Exists(Path.Combine(services, fileName)), fileName);
        }

        var productionSources = Directory.EnumerateFiles(
            GetRepositoryRoot(),
            "*.cs",
            SearchOption.AllDirectories)
            .Where(path => !path.Contains(".Test", StringComparison.Ordinal))
            .Where(path => !path.Contains(
                $"{Path.DirectorySeparatorChar}Tests.",
                StringComparison.Ordinal))
            .Where(path => !path.Contains(
                $"{Path.DirectorySeparatorChar}Tests{Path.DirectorySeparatorChar}",
                StringComparison.Ordinal))
            .Where(path => !path.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
            .Where(path => !path.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.Ordinal));
        var combined = string.Join('\n', productionSources.Select(File.ReadAllText));
        Assert.IsFalse(combined.Contains("GetUiPreferences", StringComparison.Ordinal));
        Assert.IsFalse(combined.Contains("SaveUiPreferences", StringComparison.Ordinal));
        Assert.IsFalse(combined.Contains("AppConfigFileName", StringComparison.Ordinal));
    }

    [TestMethod]
    public void FrontendAndAgentSharePreferencesWithoutForbiddenDependencies()
    {
        var root = GetRepositoryRoot();
        var frontendReferences = ReadProjectReferences(Path.Combine(
            root,
            "Common",
            "Frontend",
            "PasswordManagerLocal.Common.Frontend.csproj"));
        var agentReferences = ReadProjectReferences(Path.Combine(
            root,
            "Windows",
            "Agent",
            "PasswordManagerLocal.Windows.Agent.csproj"));

        Assert.IsTrue(frontendReferences.Any(reference => reference.Contains(
            "PasswordManagerLocal.Common.Preferences",
            StringComparison.Ordinal)));
        Assert.IsFalse(frontendReferences.Any(reference => reference.Contains(
            "PasswordManagerLocal.Common.Backend",
            StringComparison.Ordinal)));
        Assert.IsTrue(agentReferences.Any(reference => reference.Contains(
            "PasswordManagerLocal.Common.Preferences",
            StringComparison.Ordinal)));
        Assert.IsFalse(agentReferences.Any(reference => reference.Contains(
            "PasswordManagerLocal.Common.Frontend",
            StringComparison.Ordinal)));
        Assert.IsFalse(agentReferences.Any(reference => reference.Contains(
            "Avalonia",
            StringComparison.Ordinal)));
    }

    [TestMethod]
    public void AgentLanguageReaderDoesNotObserveTheme()
    {
        var agentRoot = Path.Combine(GetRepositoryRoot(), "Windows", "Agent");
        var sources = Directory.EnumerateFiles(agentRoot, "*.cs", SearchOption.AllDirectories)
            .Where(path => !path.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
            .Select(File.ReadAllText)
            .ToArray();
        var combined = string.Join('\n', sources);

        Assert.IsTrue(combined.Contains("WindowsAgentApplicationPreferencesReader", StringComparison.Ordinal));
        Assert.IsTrue(combined.Contains("preferences.Language", StringComparison.Ordinal));
        Assert.IsFalse(combined.Contains("preferences.Theme", StringComparison.Ordinal));
        Assert.IsFalse(combined.Contains("AppThemeMode", StringComparison.Ordinal));
    }

    [TestMethod]
    public void BackgroundSyncAndFirewallConfigurationRemainSeparate()
    {
        Assert.AreNotEqual(
            ApplicationFileNames.ApplicationPreferencesFileName,
            ApplicationFileNames.BackgroundSyncSettingsFileName);
        Assert.AreNotEqual(
            ApplicationFileNames.ApplicationPreferencesFileName,
            ApplicationFileNames.WindowsFirewallConfigurationFileName);

        var preferencePropertyNames = typeof(ApplicationPreferences)
            .GetProperties()
            .Select(property => property.Name)
            .ToArray();
        CollectionAssert.DoesNotContain(preferencePropertyNames, "IsBackgroundSyncEnabled");
        CollectionAssert.DoesNotContain(preferencePropertyNames, "WindowsFirewallConfigured");
    }

    private static string[] ReadProjectReferences(string projectPath) =>
        XDocument.Load(projectPath)
            .Descendants("ProjectReference")
            .Select(reference => reference.Attribute("Include")?.Value ?? string.Empty)
            .ToArray();

    private static string GetRepositoryRoot([CallerFilePath] string sourcePath = "") =>
        Path.GetFullPath(Path.Combine(Path.GetDirectoryName(sourcePath)!, "..", "..", ".."));
}
