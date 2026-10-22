using System.Text.Json;
using PasswordManagerLocal.Common.Contracts.Preferences;

namespace PasswordManagerLocal.Common.Frontend.Information;

/// <summary>Loads only bundled public documentation, once for the UI session. No runtime file/network access.</summary>
public static class HelpCatalogLoader
{
    public static HelpCatalog Load()
    {
        var english = Read("en_us");
        var hungarian = Read("hu");
        ValidateContract(english, hungarian);
        return new HelpCatalog(new Dictionary<AppLanguage, HelpCatalogContent>
        {
            [AppLanguage.English] = english,
            [AppLanguage.Hungarian] = hungarian
        });
    }

    private static HelpCatalogContent Read(string language)
    {
        using var stream = typeof(HelpCatalogLoader).Assembly.GetManifestResourceStream(
            $"PasswordManagerLocal.Common.Frontend.Assets.Help.{language}.json")
            ?? throw new InvalidDataException("A bundled help catalog is missing.");
        using var document = JsonDocument.Parse(stream);
        var root = document.RootElement;
        if (root.GetProperty("schemaVersion").GetInt32() != 1)
            throw new InvalidDataException("Unsupported help catalog schema.");

        var categories = root.GetProperty("categories").EnumerateArray()
            .Select(item => new HelpCategory(Text(item, "id"), Text(item, "title"), Text(item, "summary")))
            .ToArray();
        if (categories.Length != 4 || categories.Select(item => item.Id).Distinct().Count() != categories.Length)
            throw new InvalidDataException("Help categories must be complete and unique.");
        var categoryIds = categories.Select(item => item.Id).ToHashSet(StringComparer.Ordinal);
        var topics = new Dictionary<string, HelpTopic>(StringComparer.Ordinal);
        foreach (var item in root.GetProperty("topics").EnumerateArray())
        {
            var sections = item.GetProperty("sections").EnumerateArray().Select(section => new HelpSection(
                Text(section, "id"),
                ReadEnum<HelpSectionKind>(section, "kind"),
                ReadEnum<HelpPlatform>(section, "platform"),
                Text(section, "title"), Text(section, "body"))).ToArray();
            var topic = new HelpTopic(Text(item, "id"), Text(item, "categoryId"),
                Text(item, "title"), Text(item, "summary"), Strings(item, "keywords"), sections,
                Strings(item, "relatedTopicIds"));
            if (!categoryIds.Contains(topic.CategoryId) || sections.Length == 0 ||
                sections.Select(section => section.Id).Distinct().Count() != sections.Length ||
                !sections.Any(section => section.Platform == HelpPlatform.All) ||
                !topics.TryAdd(topic.Id, topic))
                throw new InvalidDataException($"Help topic '{topic.Id}' is invalid or duplicated.");
        }
        foreach (var topic in topics.Values)
        {
            if (topic.RelatedTopicIds.Any(id => id == topic.Id || !topics.ContainsKey(id)) ||
                topic.RelatedTopicIds.Distinct().Count() != topic.RelatedTopicIds.Count)
                throw new InvalidDataException($"Help topic '{topic.Id}' has invalid related links.");
        }
        if (categories.Any(category => !topics.Values.Any(topic => topic.CategoryId == category.Id)))
            throw new InvalidDataException("A help category has no real topics.");
        return new HelpCatalogContent(Array.AsReadOnly(categories), topics);
    }

    private static void ValidateContract(HelpCatalogContent expected, HelpCatalogContent candidate)
    {
        if (!expected.Categories.Select(item => item.Id).SequenceEqual(candidate.Categories.Select(item => item.Id)) ||
            !expected.Topics.Keys.SequenceEqual(candidate.Topics.Keys))
            throw new InvalidDataException("Help catalog IDs differ between languages.");
        foreach (var (id, topic) in expected.Topics)
        {
            var other = candidate.Topics[id];
            if (topic.CategoryId != other.CategoryId ||
                !topic.RelatedTopicIds.SequenceEqual(other.RelatedTopicIds) ||
                !topic.Sections.Select(section => (section.Id, section.Kind, section.Platform))
                    .SequenceEqual(other.Sections.Select(section => (section.Id, section.Kind, section.Platform))))
                throw new InvalidDataException($"The localized help structure for '{id}' differs.");
        }
    }

    private static string Text(JsonElement item, string property)
    {
        var text = item.GetProperty(property).GetString();
        return !string.IsNullOrWhiteSpace(text) ? text
            : throw new InvalidDataException($"Help property '{property}' must not be empty.");
    }

    private static string[] Strings(JsonElement item, string property) =>
        item.GetProperty(property).EnumerateArray().Select(value =>
            !string.IsNullOrWhiteSpace(value.GetString()) ? value.GetString()!
                : throw new InvalidDataException($"Help property '{property}' contains empty text.")).ToArray();

    private static T ReadEnum<T>(JsonElement item, string property) where T : struct, Enum =>
        Enum.TryParse<T>(Text(item, property), out var value) && Enum.IsDefined(value) ? value
            : throw new InvalidDataException($"Help property '{property}' has an unsupported value.");
}
