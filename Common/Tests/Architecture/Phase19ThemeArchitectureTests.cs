using System.Runtime.CompilerServices;
using System.Xml.Linq;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using PasswordManagerLocal.Common.Contracts.Preferences;

namespace PasswordManagerLocal.Common.Tests.Architecture;

[TestClass]
public sealed class Phase19ThemeArchitectureTests
{
    [TestMethod]
    public void StableThemeContractContainsOnlyCompleteReleasedThemes()
    {
        CollectionAssert.AreEqual(
            new[] { AppThemeId.Dark, AppThemeId.Light },
            Enum.GetValues<AppThemeId>());
        var preferenceDirectory = Path.Combine(GetRepositoryRoot(), "Common", "Contracts", "Preferences");
        Assert.IsFalse(File.Exists(Path.Combine(preferenceDirectory, "AppThemeMode.cs")));
        var combined = string.Join('\n', Directory.EnumerateFiles(
                preferenceDirectory,
                "*.cs",
                SearchOption.TopDirectoryOnly)
            .Select(File.ReadAllText));
        Assert.IsFalse(combined.Contains("Ocean", StringComparison.Ordinal));
        Assert.IsFalse(combined.Contains("Forest", StringComparison.Ordinal));
        Assert.IsFalse(combined.Contains("HighContrast", StringComparison.Ordinal));
    }

    [TestMethod]
    public void FluentRemainsInstalledAndThemeResourcesAreLoadedOnDemand()
    {
        var root = GetRepositoryRoot();
        var appXaml = File.ReadAllText(Path.Combine(root, "Common", "Frontend", "App.axaml"));
        StringAssert.Contains(appXaml, "<fluent:FluentTheme />");

        var project = XDocument.Load(Path.Combine(
            root,
            "Common",
            "Frontend",
            "PasswordManagerLocal.Common.Frontend.csproj"));
        var broadAssetRule = project.Descendants("AvaloniaResource")
            .Single(item => item.Attribute("Include")?.Value == @"Assets\**");
        Assert.AreEqual(@"Assets\**\*.axaml", broadAssetRule.Attribute("Exclude")?.Value);

        var loader = File.ReadAllText(Path.Combine(
            root, "Common", "Frontend", "Theming", "AvaloniaAppThemeResourceLoader.cs"));
        StringAssert.Contains(loader, "Func<IResourceDictionary>");
        StringAssert.Contains(loader, "factory()");
        Assert.IsFalse(loader.Contains("static readonly", StringComparison.Ordinal));
        StringAssert.Contains(loader, "[AppThemeId.Dark] = static () => new DarkThemeResources()");
        StringAssert.Contains(loader, "[AppThemeId.Light] = static () => new LightThemeResources()");
    }

    [TestMethod]
    public void ThemeChangesDoNotRecreateFrontendGraphOrBlockLifecycleCallbacks()
    {
        var root = GetRepositoryRoot();
        var themingRoot = Path.Combine(root, "Common", "Frontend", "Theming");
        var themeSources = string.Join('\n', Directory.EnumerateFiles(
                themingRoot,
                "*.cs",
                SearchOption.AllDirectories)
            .Select(File.ReadAllText));
        var preferences = File.ReadAllText(Path.Combine(
            root, "Common", "Frontend", "Services", "UiPreferencesService.cs"));
        var activity = File.ReadAllText(Path.Combine(root, "Android", "Frontend", "MainActivity.cs"));
        var session = File.ReadAllText(Path.Combine(root, "Common", "Frontend", "FrontendUiSession.cs"));

        Assert.IsFalse(themeSources.Contains("new FrontendUiSession", StringComparison.Ordinal));
        Assert.IsFalse(preferences.Contains("new FrontendUiSession", StringComparison.Ordinal));
        Assert.IsFalse(themeSources.Contains("GetAwaiter().GetResult", StringComparison.Ordinal));
        Assert.IsFalse(preferences.Contains("GetAwaiter().GetResult", StringComparison.Ordinal));
        StringAssert.Contains(activity, "_ = DisposeSessionAsync(session)");
        StringAssert.Contains(session, "_uiPreferences?.Deactivate()");
        StringAssert.Contains(session, "await _uiPreferences.DisposeAsync()");
    }

    [TestMethod]
    public void WindowsAgentHasNoFrontendOrThemeResourceDependency()
    {
        var root = GetRepositoryRoot();
        var project = XDocument.Load(Path.Combine(
            root, "Windows", "Agent", "PasswordManagerLocal.Windows.Agent.csproj"));
        var references = project.Descendants("ProjectReference")
            .Select(reference => reference.Attribute("Include")?.Value ?? string.Empty)
            .ToArray();
        Assert.IsFalse(references.Any(reference => reference.Contains(
            "Common.Frontend",
            StringComparison.OrdinalIgnoreCase)));

        var sources = string.Join('\n', Directory.EnumerateFiles(
                Path.Combine(root, "Windows", "Agent"),
                "*.cs",
                SearchOption.AllDirectories)
            .Select(File.ReadAllText));
        Assert.IsFalse(sources.Contains("Avalonia", StringComparison.Ordinal));
        Assert.IsFalse(sources.Contains("AppThemeId", StringComparison.Ordinal));
        Assert.IsFalse(sources.Contains("preferences.Theme", StringComparison.Ordinal));
    }

    private static string GetRepositoryRoot([CallerFilePath] string sourcePath = "") =>
        Path.GetFullPath(Path.Combine(Path.GetDirectoryName(sourcePath)!, "..", "..", ".."));
}
