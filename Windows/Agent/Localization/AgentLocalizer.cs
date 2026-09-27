using PasswordManagerLocal.Common.Contracts.Preferences;
using System.Diagnostics;

namespace PasswordManagerLocal.Windows.Agent.Localization;

public sealed class AgentLocalizer : IAgentLocalizer
{
    private readonly IAgentLocalizationResourceLoader _resourceLoader;
    private AgentLocalizationSnapshot _active;

    private AgentLocalizer(
        IAgentLocalizationResourceLoader resourceLoader,
        AgentLocalizationSnapshot active)
    {
        _resourceLoader = resourceLoader;
        _active = active;
    }

    public AppLanguage CurrentLanguage => Volatile.Read(ref _active).Language;

    public string GetString(string key)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        var active = Volatile.Read(ref _active);
        if (active.Translations.TryGetValue(key, out var value))
            return value;
        Trace.TraceError($"The active Agent localization resource is missing key '{key}'.");
        return $"[{key}]";
    }

    public static async Task<AgentLocalizer> CreateAsync(
        AppLanguage language,
        IAgentLocalizationResourceLoader? resourceLoader = null,
        CancellationToken cancellationToken = default)
    {
        var loader = resourceLoader ?? new EmbeddedAgentLocalizationResourceLoader();
        var snapshot = await LoadSnapshotAsync(loader, language, cancellationToken).ConfigureAwait(false);
        return new AgentLocalizer(loader, snapshot);
    }

    internal Task<AgentLocalizationSnapshot> LoadCandidateAsync(
        AppLanguage language,
        CancellationToken cancellationToken = default) =>
        LoadSnapshotAsync(_resourceLoader, language, cancellationToken);

    internal void Activate(AgentLocalizationSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        Volatile.Write(ref _active, snapshot);
    }

    private static async Task<AgentLocalizationSnapshot> LoadSnapshotAsync(
        IAgentLocalizationResourceLoader loader,
        AppLanguage language,
        CancellationToken cancellationToken)
    {
        await using var stream = loader.Open(language);
        var translations = await AgentLocalizationResourceParser.ParseAsync(stream, cancellationToken)
            .ConfigureAwait(false);
        return new AgentLocalizationSnapshot(language, translations);
    }
}

