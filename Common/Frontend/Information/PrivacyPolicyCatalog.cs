using System.Globalization;
using System.Text.Json;
using PasswordManagerLocal.Common.Contracts.Preferences;

namespace PasswordManagerLocal.Common.Frontend.Information;

/// <summary>Bundled public text only. No accounts, storage paths, endpoints or network access.</summary>
public sealed class PrivacyPolicyCatalog
{
    private readonly PrivacyPolicyDocument _english;
    private readonly PrivacyPolicyDocument _hungarian;

    private PrivacyPolicyCatalog(PrivacyPolicyDocument english, PrivacyPolicyDocument hungarian)
    {
        _english = english;
        _hungarian = hungarian;
    }

    public PrivacyPolicyDocument ForLanguage(AppLanguage language) =>
        language == AppLanguage.Hungarian ? _hungarian : _english;

    public static PrivacyPolicyCatalog Load()
    {
        var english = Read("en_us");
        var hungarian = Read("hu");
        if (english.Version != hungarian.Version || english.ReviewedOn != hungarian.ReviewedOn ||
            english.EffectiveOn != hungarian.EffectiveOn || english.IsDraft != hungarian.IsDraft ||
            !english.Sections.Select(section => section.Id).SequenceEqual(hungarian.Sections.Select(section => section.Id)))
            throw new InvalidDataException("Privacy policy translations must have matching metadata and section IDs.");
        return new PrivacyPolicyCatalog(english, hungarian);
    }

    private static PrivacyPolicyDocument Read(string language)
    {
        using var stream = typeof(PrivacyPolicyCatalog).Assembly.GetManifestResourceStream(
            $"PasswordManagerLocal.Common.Frontend.Assets.Privacy.{language}.json")
            ?? throw new InvalidDataException("A bundled privacy policy is missing.");
        using var document = JsonDocument.Parse(stream);
        var root = document.RootElement;
        var sections = root.GetProperty("sections").EnumerateArray().Select(section =>
            new PrivacyPolicySection(Text(section, "id"), Text(section, "heading"), Text(section, "body"))).ToArray();
        if (sections.Length == 0 || sections.Select(section => section.Id).Distinct(StringComparer.Ordinal).Count() != sections.Length)
            throw new InvalidDataException("Privacy policy section IDs must be nonempty and unique.");
        var effective = root.GetProperty("effectiveOn");
        var result = new PrivacyPolicyDocument(Text(root, "version"), Date(root, "reviewedOn"),
            effective.ValueKind == JsonValueKind.Null ? null : Date(root, "effectiveOn"),
            root.GetProperty("isDraft").GetBoolean(), Text(root, "title"), Text(root, "summary"),
            Text(root, "statusText"), sections);
        if (!result.IsDraft && result.EffectiveOn is null)
            throw new InvalidDataException("A final privacy policy needs an effective date.");
        return result;
    }

    private static string Text(JsonElement element, string property) =>
        element.GetProperty(property).GetString() is { } text && !string.IsNullOrWhiteSpace(text)
            ? text : throw new InvalidDataException($"Privacy policy property '{property}' is empty.");

    private static DateOnly Date(JsonElement element, string property) =>
        DateOnly.ParseExact(Text(element, property), "yyyy-MM-dd", CultureInfo.InvariantCulture);
}
