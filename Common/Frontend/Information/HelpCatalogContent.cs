namespace PasswordManagerLocal.Common.Frontend.Information;

public sealed record HelpCatalogContent(
    IReadOnlyList<HelpCategory> Categories,
    IReadOnlyDictionary<string, HelpTopic> Topics);
