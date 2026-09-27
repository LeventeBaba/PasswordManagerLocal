using PasswordManagerLocal.Common.Contracts.Preferences;
using PasswordManagerLocal.Windows.Agent.Localization;
using System.Text;
using System.Text.Json;

namespace PasswordManagerLocal.Windows.Tests.IPC.Infrastructure;

internal sealed class InMemoryAgentLocalizationResourceLoader : IAgentLocalizationResourceLoader
{
    private readonly Dictionary<AppLanguage, Func<Stream>> _resources = new();

    public int OpenCount { get; private set; }

    public void SetResource(AppLanguage language, IReadOnlyDictionary<string, string> translations)
    {
        var json = JsonSerializer.Serialize(translations);
        _resources[language] = () => new MemoryStream(Encoding.UTF8.GetBytes(json), writable: false);
    }

    public void SetFailure(AppLanguage language, Exception exception) =>
        _resources[language] = () => throw exception;

    public Stream Open(AppLanguage language)
    {
        OpenCount++;
        return _resources.TryGetValue(language, out var factory)
            ? factory()
            : throw new InvalidDataException($"No test resource exists for {language}.");
    }

    public static Dictionary<string, string> CreateValidTranslations(string prefix)
    {
        return AgentLocalizationKeys.All.ToDictionary(
            key => key,
            key => $"{prefix}:{key}",
            StringComparer.Ordinal);
    }
}
