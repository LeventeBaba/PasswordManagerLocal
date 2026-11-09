using System.Globalization;
using System.Text;

namespace PasswordManagerLocal.Common.Frontend.Localization;

public static class FrontendTranslationContractValidator
{
    public static IReadOnlyList<FrontendTranslationContractViolation> ValidateResource(
        IReadOnlyDictionary<string, string> translations)
    {
        ArgumentNullException.ThrowIfNull(translations);
        var violations = new List<FrontendTranslationContractViolation>();
        if (translations.Count == 0)
        {
            violations.Add(new FrontendTranslationContractViolation(
                FrontendTranslationContractViolationKind.EmptyResource,
                null));
            return violations;
        }

        foreach (var (key, value) in translations.OrderBy(pair => pair.Key, StringComparer.Ordinal))
        {
            if (string.IsNullOrWhiteSpace(key))
            {
                violations.Add(new FrontendTranslationContractViolation(
                    FrontendTranslationContractViolationKind.EmptyKey,
                    null));
                continue;
            }

            if (string.IsNullOrWhiteSpace(value))
            {
                violations.Add(new FrontendTranslationContractViolation(
                    FrontendTranslationContractViolationKind.EmptyValue,
                    key));
                continue;
            }

            if (!TryReadPlaceholderIdentities(value, out _, out var isCompositeFormatValid) ||
                !isCompositeFormatValid)
            {
                violations.Add(new FrontendTranslationContractViolation(
                    FrontendTranslationContractViolationKind.InvalidFormat,
                    key));
            }
        }

        return violations;
    }

    public static IReadOnlyList<FrontendTranslationContractViolation> Compare(
        IReadOnlyDictionary<string, string> expected,
        IReadOnlyDictionary<string, string> candidate)
    {
        ArgumentNullException.ThrowIfNull(expected);
        ArgumentNullException.ThrowIfNull(candidate);

        var violations = new List<FrontendTranslationContractViolation>();
        violations.AddRange(ValidateResource(expected));
        violations.AddRange(ValidateResource(candidate));

        foreach (var key in expected.Keys.Except(candidate.Keys, StringComparer.Ordinal)
                     .OrderBy(key => key, StringComparer.Ordinal))
        {
            violations.Add(new FrontendTranslationContractViolation(
                FrontendTranslationContractViolationKind.MissingKey,
                key));
        }

        foreach (var key in candidate.Keys.Except(expected.Keys, StringComparer.Ordinal)
                     .OrderBy(key => key, StringComparer.Ordinal))
        {
            violations.Add(new FrontendTranslationContractViolation(
                FrontendTranslationContractViolationKind.UnexpectedKey,
                key));
        }

        foreach (var key in expected.Keys.Intersect(candidate.Keys, StringComparer.Ordinal)
                     .OrderBy(key => key, StringComparer.Ordinal))
        {
            if (!TryReadPlaceholderIdentities(expected[key], out var expectedPlaceholders, out _) ||
                !TryReadPlaceholderIdentities(candidate[key], out var candidatePlaceholders, out _))
            {
                continue;
            }

            if (!expectedPlaceholders.SequenceEqual(candidatePlaceholders, StringComparer.Ordinal))
            {
                violations.Add(new FrontendTranslationContractViolation(
                    FrontendTranslationContractViolationKind.PlaceholderMismatch,
                    key));
            }
        }

        return violations;
    }

    private static bool TryReadPlaceholderIdentities(
        string value,
        out IReadOnlyList<string> identities,
        out bool isCompositeFormatValid)
    {
        var result = new List<string>();
        for (var index = 0; index < value.Length; index++)
        {
            if (value[index] == '{')
            {
                if (index + 1 < value.Length && value[index + 1] == '{')
                {
                    index++;
                    continue;
                }

                var closingBrace = value.IndexOf('}', index + 1);
                if (closingBrace < 0)
                {
                    identities = [];
                    isCompositeFormatValid = false;
                    return false;
                }

                var body = value.AsSpan(index + 1, closingBrace - index - 1);
                var separator = body.IndexOfAny(',', ':');
                var identity = (separator < 0 ? body : body[..separator]).Trim().ToString();
                if (!IsPlaceholderIdentity(identity))
                {
                    identities = [];
                    isCompositeFormatValid = false;
                    return false;
                }

                result.Add(identity);
                index = closingBrace;
                continue;
            }

            if (value[index] == '}')
            {
                if (index + 1 < value.Length && value[index + 1] == '}')
                {
                    index++;
                    continue;
                }

                identities = [];
                isCompositeFormatValid = false;
                return false;
            }
        }

        result.Sort(StringComparer.Ordinal);
        identities = result;
        try
        {
            _ = CompositeFormat.Parse(value);
            isCompositeFormatValid = true;
        }
        catch (FormatException)
        {
            // Named placeholders are still extracted above so contract comparison can
            // distinguish a name mismatch from a mere placeholder-count mismatch. They
            // remain invalid for the application's numeric string.Format contract.
            isCompositeFormatValid = false;
        }

        return true;
    }

    private static bool IsPlaceholderIdentity(string identity)
    {
        if (int.TryParse(
                identity,
                NumberStyles.None,
                CultureInfo.InvariantCulture,
                out var numericIdentity))
            return numericIdentity >= 0;
        if (identity.Length == 0 || !(char.IsLetter(identity[0]) || identity[0] == '_'))
            return false;
        return identity.All(character =>
            char.IsLetterOrDigit(character) || character is '_' or '.');
    }
}
