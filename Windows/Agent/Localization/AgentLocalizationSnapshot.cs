using PasswordManagerLocal.Common.Contracts.Preferences;

namespace PasswordManagerLocal.Windows.Agent.Localization;

internal sealed record AgentLocalizationSnapshot(
    AppLanguage Language,
    IReadOnlyDictionary<string, string> Translations);
