using PasswordManagerLocal.Common.Contracts.Preferences;

namespace PasswordManagerLocal.Common.Frontend.Information;

public sealed class HelpCatalog
{
    private readonly IReadOnlyDictionary<AppLanguage, HelpCatalogContent> _languages;
    private readonly Dictionary<(AppLanguage, HelpPlatform), HelpSearchIndex> _searchIndexes = new();

    internal HelpCatalog(IReadOnlyDictionary<AppLanguage, HelpCatalogContent> languages) =>
        _languages = languages;

    public HelpCatalogContent ForLanguage(AppLanguage language) =>
        _languages.TryGetValue(language, out var content) ? content : _languages[AppLanguage.English];

    public IReadOnlyList<HelpSearchResult> Search(AppLanguage language, HelpPlatform platform, string? query)
    {
        if (string.IsNullOrWhiteSpace(query))
            return [];
        var key = (language, platform);
        if (!_searchIndexes.TryGetValue(key, out var index))
        {
            index = new HelpSearchIndex(ForLanguage(language), platform);
            _searchIndexes.Add(key, index);
        }
        return index.Search(query);
    }
}
