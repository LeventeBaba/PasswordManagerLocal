using System.Globalization;
using System.Text;
using System.Text.Json;

namespace PasswordManagerLocal.Windows.Agent.Localization;

public static class AgentLocalizationResourceParser
{
    public static async Task<IReadOnlyDictionary<string, string>> ParseAsync(
        Stream stream,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(stream);
        using var document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken)
            .ConfigureAwait(false);
        if (document.RootElement.ValueKind != JsonValueKind.Object)
            throw new InvalidDataException("An Agent localization resource must contain a JSON object.");

        var translations = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var property in document.RootElement.EnumerateObject())
        {
            if (!translations.TryAdd(property.Name, ReadValue(property)))
                throw new InvalidDataException($"Duplicate Agent localization key: {property.Name}");
        }

        ValidateDictionary(translations);
        return new System.Collections.ObjectModel.ReadOnlyDictionary<string, string>(translations);
    }

    public static void ValidateDictionary(IReadOnlyDictionary<string, string> translations)
    {
        ArgumentNullException.ThrowIfNull(translations);
        var unknown = translations.Keys.Except(AgentLocalizationKeys.All, StringComparer.Ordinal).ToArray();
        if (unknown.Length != 0)
            throw new InvalidDataException($"Unknown Agent localization key: {unknown[0]}");
        var missing = AgentLocalizationKeys.All.Except(translations.Keys, StringComparer.Ordinal).ToArray();
        if (missing.Length != 0)
            throw new InvalidDataException($"Missing Agent localization key: {missing[0]}");

        foreach (var pair in translations)
        {
            if (string.IsNullOrWhiteSpace(pair.Value))
                throw new InvalidDataException($"Agent localization value is empty: {pair.Key}");
            _ = CompositeFormat.Parse(pair.Value);
        }
    }

    public static IReadOnlySet<int> GetPlaceholderSet(string value)
    {
        ArgumentNullException.ThrowIfNull(value);
        _ = CompositeFormat.Parse(value);
        var result = new HashSet<int>();
        for (var index = 0; index < value.Length; index++)
        {
            if (value[index] == '{')
            {
                if (index + 1 < value.Length && value[index + 1] == '{')
                {
                    index++;
                    continue;
                }

                var start = ++index;
                while (index < value.Length && char.IsDigit(value[index]))
                    index++;
                if (start == index || !int.TryParse(value.AsSpan(start, index - start), NumberStyles.None, CultureInfo.InvariantCulture, out var placeholder))
                    throw new FormatException("The composite-format placeholder is invalid.");
                result.Add(placeholder);
            }
            else if (value[index] == '}' && index + 1 < value.Length && value[index + 1] == '}')
            {
                index++;
            }
        }
        return result;
    }

    private static string ReadValue(JsonProperty property)
    {
        if (property.Value.ValueKind != JsonValueKind.String)
            throw new InvalidDataException($"Agent localization value must be a string: {property.Name}");
        return property.Value.GetString()
            ?? throw new InvalidDataException($"Agent localization value is null: {property.Name}");
    }
}
