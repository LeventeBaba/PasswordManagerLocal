using System.Globalization;
using System.Text;

namespace PasswordManagerLocal.Common.Frontend.Information;

/// <summary>An immutable index of bundled guidance for one language and platform.</summary>
public sealed class HelpSearchIndex
{
    private readonly Document[] _documents;

    public HelpSearchIndex(HelpCatalogContent content, HelpPlatform platform)
    {
        var categories = content.Categories.ToDictionary(category => category.Id);
        _documents = content.Topics.Values.Select(topic =>
        {
            var category = categories[topic.CategoryId].Title;
            var sections = topic.Sections
                .Where(section => section.Platform == HelpPlatform.All || section.Platform == platform)
                .Select(section => new Section(section.Body, Normalize(section.Title), Normalize(section.Body)))
                .ToArray();
            return new Document(topic, category, Normalize(category), Normalize(topic.Title),
                Normalize(topic.Summary), topic.Keywords.Select(Normalize).ToArray(), sections);
        }).ToArray();
    }

    public IReadOnlyList<HelpSearchResult> Search(string? query)
    {
        var normalized = Normalize(query ?? string.Empty);
        if (normalized.Length == 0)
            return [];
        var terms = normalized.Split(' ', StringSplitOptions.RemoveEmptyEntries).Distinct().ToArray();
        return _documents.Select(document => (Document: document, Score: Score(document, normalized, terms)))
            .Where(match => match.Score > 0)
            .OrderByDescending(match => match.Score)
            .Select(match => new HelpSearchResult(match.Document.Topic, match.Document.Category,
                Preview(match.Document, terms)))
            .ToArray();
    }

    private static int Score(Document document, string query, string[] terms)
    {
        var score = 0;
        foreach (var term in terms)
        {
            var weight = document.Title.Contains(term, StringComparison.Ordinal) ? 120
                : document.Keywords.Any(keyword => keyword.Contains(term, StringComparison.Ordinal)) ? 80
                : document.Summary.Contains(term, StringComparison.Ordinal) ? 50
                : document.Sections.Any(section => section.Title.Contains(term, StringComparison.Ordinal)) ? 35
                : document.Sections.Any(section => section.Text.Contains(term, StringComparison.Ordinal)) ? 10
                : document.CategoryText.Contains(term, StringComparison.Ordinal) ? 5 : 0;
            if (weight == 0)
                return 0; // Every query word must match somewhere in this topic.
            score += weight;
        }
        if (document.Title == query)
            score += 500;
        else if (document.Title.StartsWith(query, StringComparison.Ordinal))
            score += 250;
        if (document.Keywords.Contains(query, StringComparer.Ordinal))
            score += 200;
        return score;
    }

    private static string Preview(Document document, string[] terms)
    {
        var section = document.Sections
            .Select(item => (Section: item, Matches: terms.Count(term => item.Text.Contains(term, StringComparison.Ordinal))))
            .OrderByDescending(item => item.Matches)
            .FirstOrDefault();
        var text = section.Matches > 0 ? section.Section.Body : document.Topic.Summary;
        if (text.Length <= 220)
            return text;

        var firstMatch = terms.Select(term => CultureInfo.InvariantCulture.CompareInfo.IndexOf(
                text, term, CompareOptions.IgnoreCase | CompareOptions.IgnoreNonSpace))
            .Where(index => index >= 0).DefaultIfEmpty(0).Min();
        var start = Math.Max(0, firstMatch - 60);
        var minimumStart = Math.Max(0, start - 40);
        while (start > minimumStart && !char.IsWhiteSpace(text[start - 1]))
            start--;
        var endLimit = Math.Min(text.Length, start + 220);
        var end = endLimit;
        while (end < text.Length && end > start && !char.IsWhiteSpace(text[end]))
            end--;
        if (end == start)
            end = endLimit; // A long unbroken word must not produce an empty/missing-match preview.
        return (start > 0 ? "…" : string.Empty) + text[start..end].Trim() +
            (end < text.Length ? "…" : string.Empty);
    }

    private static string Normalize(string text)
    {
        var result = new StringBuilder(text.Length);
        foreach (var character in text.Normalize(NormalizationForm.FormD))
        {
            if (CharUnicodeInfo.GetUnicodeCategory(character) == UnicodeCategory.NonSpacingMark)
                continue;
            if (char.IsLetterOrDigit(character))
                result.Append(char.ToUpperInvariant(character));
            else if (result.Length > 0 && result[^1] != ' ')
                result.Append(' ');
        }
        return result.ToString().Trim();
    }

    private sealed record Section(string Body, string Title, string Text);
    private sealed record Document(HelpTopic Topic, string Category, string CategoryText, string Title,
        string Summary, string[] Keywords, Section[] Sections);
}
