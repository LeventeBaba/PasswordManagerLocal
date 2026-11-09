using System.Collections.Frozen;
using System.Text.Json;

namespace PasswordManagerLocal.Common.Frontend.Localization;

internal static class FrontendTranslationResourceParser
{
    public static async Task<FrozenDictionary<string, string>> ParseAsync(
        Stream stream,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(stream);
        using var document = await JsonDocument.ParseAsync(
            stream,
            new JsonDocumentOptions
            {
                AllowTrailingCommas = false,
                CommentHandling = JsonCommentHandling.Disallow
            },
            cancellationToken).ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();

        if (document.RootElement.ValueKind != JsonValueKind.Object)
            throw new InvalidDataException("A frontend localization resource must be a JSON object.");

        var translations = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var property in document.RootElement.EnumerateObject())
        {
            if (property.Value.ValueKind != JsonValueKind.String ||
                !translations.TryAdd(property.Name, property.Value.GetString()!))
            {
                throw new InvalidDataException("A frontend localization resource contains an invalid entry.");
            }
        }

        var violations = FrontendTranslationContractValidator.ValidateResource(translations);
        if (violations.Count != 0)
            throw new InvalidDataException("A frontend localization resource violates its formatting contract.");

        return translations.ToFrozenDictionary(StringComparer.Ordinal);
    }
}
