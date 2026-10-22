using PasswordManagerLocal.Common.Contracts.Preferences;

namespace PasswordManagerLocal.Common.Frontend.Information;

public sealed class HelpCatalog
{
    private readonly IReadOnlyDictionary<AppLanguage, HelpCatalogContent> _languages;

    internal HelpCatalog(IReadOnlyDictionary<AppLanguage, HelpCatalogContent> languages) =>
        _languages = languages;

    public HelpCatalogContent ForLanguage(AppLanguage language) =>
        _languages.TryGetValue(language, out var content) ? content : _languages[AppLanguage.English];
}
