using Microsoft.VisualStudio.TestTools.UnitTesting;
using PasswordManagerLocal.Common.Contracts.Preferences;
using PasswordManagerLocal.Windows.Agent.Localization;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace PasswordManagerLocal.Windows.Tests.IPC.Dependency;

[TestClass]
public sealed class AgentLocalizationArchitectureTests
{
    [TestMethod]
    public void EverySupportedLanguageHasExactlyOneDeterministicallyMappedAgentResource()
    {
        var directory = Path.Combine(
            GetRepositoryRoot(), "Windows", "Agent", "Assets", "Localization");
        var expected = Enum.GetValues<AppLanguage>()
            .Select(EmbeddedAgentLocalizationResourceLoader.GetFileName)
            .OrderBy(value => value, StringComparer.Ordinal)
            .ToArray();
        var actual = Directory.EnumerateFiles(directory, "*.json", SearchOption.TopDirectoryOnly)
            .Select(Path.GetFileName)
            .OrderBy(value => value, StringComparer.Ordinal)
            .ToArray();

        CollectionAssert.AreEqual(expected, actual);
        Assert.AreEqual(expected.Length, expected.Distinct(StringComparer.Ordinal).Count());
    }

    [TestMethod]
    public void AgentLocalizationDoesNotReadThemeOrFrontendLocalization()
    {
        var agentDirectory = Path.Combine(GetRepositoryRoot(), "Windows", "Agent");
        var source = ReadSources(agentDirectory);

        Assert.IsFalse(source.Contains("AppThemeId", StringComparison.Ordinal));
        Assert.IsFalse(source.Contains("preferences.Theme", StringComparison.Ordinal));
        Assert.IsFalse(source.Contains("LocalizationManager", StringComparison.Ordinal));
        Assert.IsFalse(source.Contains("Common.Frontend", StringComparison.Ordinal));
        Assert.IsFalse(source.Contains("Avalonia", StringComparison.Ordinal));
        Assert.IsFalse(source.Contains("System.Windows.Forms", StringComparison.Ordinal));
    }

    [TestMethod]
    public void LocalizationAddsNoPollingWatcherOrRecurringTimer()
    {
        var root = GetRepositoryRoot();
        var source = string.Join(
            Environment.NewLine,
            ReadSources(Path.Combine(root, "Windows", "Agent", "Localization")),
            ReadSources(Path.Combine(root, "Windows", "Agent", "Preferences")));

        Assert.IsFalse(source.Contains("FileSystemWatcher", StringComparison.Ordinal));
        Assert.IsFalse(source.Contains("PeriodicTimer", StringComparison.Ordinal));
        Assert.IsFalse(source.Contains("System.Threading.Timer", StringComparison.Ordinal));
        Assert.IsFalse(source.Contains("Task.Delay", StringComparison.Ordinal));
        Assert.IsFalse(source.Contains("Thread.Sleep", StringComparison.Ordinal));
    }

    [TestMethod]
    public void NativeTrayUsesCurrentLocalizedTextAndDestroysEveryOnDemandMenu()
    {
        var tray = File.ReadAllText(Path.Combine(
            GetRepositoryRoot(), "Windows", "Agent", "Tray", "WindowsNativeTrayIconAdapter.cs"));

        StringAssert.Contains(tray, "Publish(ContextMenuOpening)");
        StringAssert.Contains(tray, "var text = _text");
        StringAssert.Contains(tray, "text.OpenLabel");
        StringAssert.Contains(tray, "text.ExitLabel");
        StringAssert.Contains(tray, "NimModify");
        StringAssert.Contains(tray, "NifTip");
        StringAssert.Contains(tray, "CreatePopupMenu");
        StringAssert.Contains(tray, "DestroyMenu(menu)");
        Assert.IsFalse(tray.Contains("static readonly WindowsAgentTrayText", StringComparison.Ordinal));
    }

    [TestMethod]
    public void AgentSourceDoesNotContainLocalizedUserVisibleResourceValues()
    {
        var root = GetRepositoryRoot();
        var source = ReadSources(Path.Combine(root, "Windows", "Agent"));
        foreach (var file in Directory.EnumerateFiles(
            Path.Combine(root, "Windows", "Agent", "Assets", "Localization"),
            "*.json"))
        {
            using var document = JsonDocument.Parse(File.ReadAllText(file));
            foreach (var property in document.RootElement.EnumerateObject())
            {
                var value = property.Value.GetString()!;
                var escapedValue = value
                    .Replace("\\", "\\\\", StringComparison.Ordinal)
                    .Replace("\"", "\\\"", StringComparison.Ordinal);
                var csharpLiteral = "\"" + escapedValue + "\"";
                Assert.IsFalse(
                    source.Contains(csharpLiteral, StringComparison.Ordinal),
                    $"Localized value remains hard-coded in Agent source: {property.Name}");
            }
        }
    }

    [TestMethod]
    public void AgentUiSurfacesDoNotReceiveHardCodedStringLiterals()
    {
        var source = ReadSources(Path.Combine(GetRepositoryRoot(), "Windows", "Agent"));
        var forbiddenPatterns = new[]
        {
            "WindowsNativeMessageBox\\.ShowError\\s*\\(\\s*\"",
            "(?:ShowErrorAsync|ShowExitFailureAsync|MarkFailed)\\s*\\(\\s*\"",
            "SafeMessage\\s*:\\s*\"",
            "(?:Info|InfoTitle|Tip)\\s*=\\s*\"",
            "AppendMenu\\s*\\([^,\\r\\n]+,[^,\\r\\n]+,[^,\\r\\n]+,\\s*\"",
            "new\\s+Ipc(?:Error|FailureDto)\\s*\\([^;]*?\""
        };

        foreach (var pattern in forbiddenPatterns)
        {
            Assert.IsFalse(
                Regex.IsMatch(source, pattern, RegexOptions.Singleline | RegexOptions.CultureInvariant),
                $"An Agent UI/safe-message surface still receives a hard-coded string literal: {pattern}");
        }
    }

    private static string ReadSources(string directory) =>
        string.Join(
            Environment.NewLine,
            Directory.EnumerateFiles(directory, "*.cs", SearchOption.AllDirectories)
                // SDK-generated AssemblyInfo files echo Product and Title metadata from the project.
                // Those generated literals are not authored Agent UI strings.
                .Where(path => !Path.GetRelativePath(directory, path)
                    .Split(Path.DirectorySeparatorChar)
                    .Any(segment =>
                        string.Equals(segment, "bin", StringComparison.OrdinalIgnoreCase) ||
                        string.Equals(segment, "obj", StringComparison.OrdinalIgnoreCase)))
                .Select(File.ReadAllText));

    private static string GetRepositoryRoot([CallerFilePath] string sourceFilePath = "") =>
        Path.GetFullPath(Path.Combine(Path.GetDirectoryName(sourceFilePath)!, "..", "..", ".."));
}
