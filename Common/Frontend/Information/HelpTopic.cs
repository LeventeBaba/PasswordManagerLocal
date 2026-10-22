namespace PasswordManagerLocal.Common.Frontend.Information;

public sealed record HelpTopic(
    string Id,
    string CategoryId,
    string Title,
    string Summary,
    IReadOnlyList<string> Keywords,
    IReadOnlyList<HelpSection> Sections,
    IReadOnlyList<string> RelatedTopicIds);
