using Microsoft.VisualStudio.TestTools.UnitTesting;
using PasswordManagerLocal.Common.Contracts.Preferences;
using PasswordManagerLocal.Windows.Agent.Localization;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Xml.Linq;

namespace PasswordManagerLocal.Windows.Tests.IPC.Dependency;

[TestClass]
public sealed class Phase5AgentLocalizationArchitectureTests
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
    public void AgentProjectEmbedsLocalizationAndKeepsFrontendAndManagedUiOut()
    {
        var root = GetRepositoryRoot();
        var project = XDocument.Load(Path.Combine(
            root, "Windows", "Agent", "PasswordManagerLocal.Windows.Agent.csproj"));
        var projectText = project.ToString();
        var references = project.Descendants("ProjectReference")
            .Select(element => element.Attribute("Include")?.Value ?? string.Empty)
            .ToArray();

        StringAssert.Contains(projectText, "Assets\\Localization\\*.json");
        Assert.IsFalse(references.Any(reference => reference.Contains("Frontend", StringComparison.OrdinalIgnoreCase)));
        Assert.IsFalse(projectText.Contains("Avalonia", StringComparison.OrdinalIgnoreCase));
        Assert.IsFalse(projectText.Contains("WindowsForms", StringComparison.OrdinalIgnoreCase));
        Assert.IsFalse(projectText.Contains("UseWPF", StringComparison.OrdinalIgnoreCase));
        Assert.IsFalse(projectText.Contains("ReactiveUI", StringComparison.OrdinalIgnoreCase));
    }

    [TestMethod]
    public void WindowsFrontendRemainsBackendAndEndpointServerIndependent()
    {
        var project = XDocument.Load(Path.Combine(
            GetRepositoryRoot(), "Windows", "Frontend", "PasswordManagerLocal.Windows.Frontend.csproj"));
        var references = project.Descendants("ProjectReference")
            .Select(element => Path.GetFileName(element.Attribute("Include")?.Value ?? string.Empty))
            .ToArray();

        Assert.IsFalse(references.Contains("PasswordManagerLocal.Common.Backend.csproj", StringComparer.OrdinalIgnoreCase));
        Assert.IsFalse(references.Contains("PasswordManagerLocal.Common.Backend.Hosting.csproj", StringComparer.OrdinalIgnoreCase));
        Assert.IsFalse(references.Contains("PasswordManagerLocal.Windows.Backend.csproj", StringComparer.OrdinalIgnoreCase));
        Assert.IsFalse(references.Contains("PasswordManagerLocal.Windows.EndpointRpc.Server.csproj", StringComparer.OrdinalIgnoreCase));
        Assert.IsTrue(references.Contains("PasswordManagerLocal.Windows.EndpointRpc.Client.csproj", StringComparer.OrdinalIgnoreCase));
        Assert.IsTrue(references.Contains("PasswordManagerLocal.Windows.EndpointRpc.Contracts.csproj", StringComparer.OrdinalIgnoreCase) ||
            File.ReadAllText(Path.Combine(
                GetRepositoryRoot(),
                "Windows", "EndpointRpc", "Client", "PasswordManagerLocal.Windows.EndpointRpc.Client.csproj"))
                .Contains("EndpointRpc.Contracts", StringComparison.Ordinal));
    }

    [TestMethod]
    public void AgentLocalizationDoesNotReadThemeOrFrontendLocalization()
    {
        var agentDirectory = Path.Combine(GetRepositoryRoot(), "Windows", "Agent");
        var source = ReadSources(agentDirectory);

        Assert.IsFalse(source.Contains("AppThemeMode", StringComparison.Ordinal));
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
                if (value == "PasswordManagerLocal")
                    continue;
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

    [TestMethod]
    public void PreferenceReloadLifetimeStopsBeforeNativeTrayDisposal()
    {
        var host = File.ReadAllText(Path.Combine(
            GetRepositoryRoot(), "Windows", "Agent", "Hosting", "WindowsAgentHost.cs"));
        var reloadStop = host.IndexOf(
            "_applicationPreferencesReloadCoordinator.DisposeAsync()",
            StringComparison.Ordinal);
        var trayStop = host.IndexOf("_trayIcon.DisposeAsync()", StringComparison.Ordinal);

        Assert.IsTrue(reloadStop >= 0);
        Assert.IsTrue(trayStop > reloadStop);
    }

    [TestMethod]
    public void ReloadProtocolCarriesNoLanguageOrThemePayloadAndUsesAuthoritativeReader()
    {
        var root = GetRepositoryRoot();
        var handler = File.ReadAllText(Path.Combine(
            root, "Windows", "Agent", "Preferences", "ReloadApplicationPreferencesWindowsIpcRequestHandler.cs"));
        var coordinator = File.ReadAllText(Path.Combine(
            root, "Windows", "Agent", "Preferences", "AgentApplicationPreferencesReloadCoordinator.cs"));
        var notifier = File.ReadAllText(Path.Combine(
            root, "Windows", "Frontend", "Settings", "WindowsAgentApplicationPreferencesNotifier.cs"));

        StringAssert.Contains(handler, "EnsureNoPayload");
        Assert.IsFalse(handler.Contains("AppLanguage", StringComparison.Ordinal));
        Assert.IsFalse(handler.Contains("AppThemeMode", StringComparison.Ordinal));
        StringAssert.Contains(coordinator, "ReadLanguageAsync");
        StringAssert.Contains(notifier, "ReloadApplicationPreferencesAsync");
        Assert.IsFalse(notifier.Contains("AppLanguage", StringComparison.Ordinal));
        Assert.IsFalse(notifier.Contains("AppThemeMode", StringComparison.Ordinal));
    }

    [TestMethod]
    public void ActiveLocalizerHasNoPerLanguageDictionaryCacheOrPermanentFallbackDictionary()
    {
        var source = File.ReadAllText(Path.Combine(
            GetRepositoryRoot(), "Windows", "Agent", "Localization", "AgentLocalizer.cs"));

        StringAssert.Contains(source, "AgentLocalizationSnapshot _active");
        Assert.IsFalse(source.Contains("Dictionary<AppLanguage", StringComparison.Ordinal));
        Assert.IsFalse(source.Contains("ConcurrentDictionary<AppLanguage", StringComparison.Ordinal));
        Assert.IsFalse(source.Contains("fallback", StringComparison.OrdinalIgnoreCase));
        Assert.IsFalse(source.Contains("static readonly", StringComparison.OrdinalIgnoreCase));
        Assert.IsFalse(source.Contains("CreateDefault", StringComparison.Ordinal));
    }

    private static string ReadSources(string directory) =>
        string.Join(
            Environment.NewLine,
            Directory.EnumerateFiles(directory, "*.cs", SearchOption.AllDirectories)
                .Select(File.ReadAllText));

    private static string GetRepositoryRoot([CallerFilePath] string sourceFilePath = "") =>
        Path.GetFullPath(Path.Combine(Path.GetDirectoryName(sourceFilePath)!, "..", "..", ".."));
}
