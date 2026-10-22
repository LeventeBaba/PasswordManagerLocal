namespace PasswordManagerLocal.Common.Frontend.Information;

public sealed record HelpSection(
    string Id, HelpSectionKind Kind, HelpPlatform Platform, string Title, string Body);
