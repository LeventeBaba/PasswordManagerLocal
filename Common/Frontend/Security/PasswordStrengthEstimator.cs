using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace PasswordManagerLocal.Common.Frontend.Security;

public sealed class PasswordStrengthEstimator
{
    private static readonly HashSet<string> CommonPasswords = new(StringComparer.Ordinal)
    {
        "1234", "12345", "123456", "1234567", "12345678", "123456789", "1234567890",
        "111111", "000000", "121212", "654321", "qwerty", "qwertz", "asdfgh", "zxcvbn",
        "password", "password1", "password123", "passw0rd", "admin", "administrator", "welcome",
        "welcome1", "letmein", "login", "master", "secret", "iloveyou", "abc123", "monkey",
        "dragon", "football", "baseball", "sunshine", "princess", "trustno1", "whatever",
        "correcthorsebatterystaple", "jelszo", "jelszo1", "jelszo123", "titok", "szeretlek",
        "magyarorszag", "budapest", "felhasznalo", "mesterjelszo"
    };

    private static readonly string[] CommonFragments =
    [
        "password", "passwort", "jelszo", "admin", "welcome", "qwerty", "qwertz", "letmein",
        "secret", "master", "login", "szeretlek", "iloveyou"
    ];

    private static readonly string[] KeyboardRows =
    [
        "1234567890", "qwertyuiop", "qwertzuiop", "asdfghjkl", "zxcvbnm"
    ];

    private static readonly Regex YearPattern = new(@"(?:19|20)\d{2}", RegexOptions.CultureInvariant);
    private static readonly Regex CompactDatePattern = new(@"(?:\d{6}|\d{8})", RegexOptions.CultureInvariant);
    private static readonly Regex RelatedTermSeparator = new(@"[^\p{L}\p{Nd}]+", RegexOptions.CultureInvariant);

    public PasswordStrengthResult Evaluate(string? password, IEnumerable<string?>? relatedTerms = null)
    {
        if (string.IsNullOrWhiteSpace(password))
            return new PasswordStrengthResult(0, 0);

        var runes = password.EnumerateRunes().ToArray();
        if (runes.Length == 0)
            return new PasswordStrengthResult(0, 0);

        var normalized = Normalize(password, convertLeet: false);
        var leetNormalized = Normalize(password, convertLeet: true);
        var isCommonPassword = ContainsCommonPassword(normalized) || ContainsCommonPassword(leetNormalized);
        var estimatedBits = CalculateBaseEntropyBits(runes);
        estimatedBits -= CalculatePatternPenalty(password, normalized, leetNormalized, runes);
        estimatedBits = ApplyRepeatedPatternLimit(estimatedBits, runes);

        if (isCommonPassword)
            estimatedBits = Math.Min(estimatedBits, 8);

        if (ContainsRelatedTerm(normalized, relatedTerms))
            estimatedBits = Math.Min(35, Math.Max(1, estimatedBits - 18));

        estimatedBits = Math.Max(1, estimatedBits);
        var score = ApplyLengthLimit(MapEntropyToScore(estimatedBits), runes.Length);

        if (isCommonPassword)
            score = 1;

        return new PasswordStrengthResult(score, estimatedBits);
    }

    private static double CalculateBaseEntropyBits(IReadOnlyCollection<Rune> runes)
    {
        var hasLowercase = false;
        var hasUppercase = false;
        var hasDigit = false;
        var hasWhitespace = false;
        var hasAsciiSymbol = false;
        var hasNonAsciiLetter = false;
        var hasOtherUnicode = false;

        foreach (var rune in runes)
        {
            if (Rune.IsLower(rune) && rune.Value <= 0x7F)
                hasLowercase = true;
            else if (Rune.IsUpper(rune) && rune.Value <= 0x7F)
                hasUppercase = true;
            else if (Rune.IsDigit(rune) && rune.Value <= 0x7F)
                hasDigit = true;
            else if (Rune.IsWhiteSpace(rune))
                hasWhitespace = true;
            else if (Rune.IsLetter(rune))
                hasNonAsciiLetter = true;
            else if (rune.Value <= 0x7F)
                hasAsciiSymbol = true;
            else
                hasOtherUnicode = true;
        }

        var poolSize = 0;
        if (hasLowercase)
            poolSize += 26;
        if (hasUppercase)
            poolSize += 26;
        if (hasDigit)
            poolSize += 10;
        if (hasAsciiSymbol)
            poolSize += 33;
        if (hasWhitespace)
            poolSize += 1;
        if (hasNonAsciiLetter)
            poolSize += 40;
        if (hasOtherUnicode)
            poolSize += 20;

        poolSize = Math.Max(poolSize, 10);
        return runes.Count * Math.Log2(poolSize);
    }

    private static double CalculatePatternPenalty(
        string password,
        string normalized,
        string leetNormalized,
        IReadOnlyList<Rune> runes)
    {
        var penalty = 0d;
        penalty += CalculateRepeatedCharacterPenalty(runes);
        penalty += CalculateSequencePenalty(normalized);
        penalty += CalculateKeyboardPenalty(normalized);
        penalty += CalculateLowVarietyPenalty(runes);

        if (YearPattern.IsMatch(password))
            penalty += 10;
        if (CompactDatePattern.IsMatch(normalized))
            penalty += 8;

        foreach (var fragment in CommonFragments)
        {
            if (normalized.Contains(fragment, StringComparison.Ordinal) ||
                leetNormalized.Contains(fragment, StringComparison.Ordinal))
            {
                penalty += 14;
            }
        }

        return penalty;
    }

    private static double CalculateRepeatedCharacterPenalty(IReadOnlyList<Rune> runes)
    {
        var penalty = 0d;
        var runLength = 1;

        for (var index = 1; index < runes.Count; index++)
        {
            if (runes[index] == runes[index - 1])
            {
                runLength++;
                continue;
            }

            if (runLength >= 3)
                penalty += (runLength - 2) * 4;

            runLength = 1;
        }

        if (runLength >= 3)
            penalty += (runLength - 2) * 4;

        return penalty;
    }

    private static double CalculateSequencePenalty(string normalized)
    {
        if (normalized.Length < 3)
            return 0;

        var penalty = 0d;
        var sequenceLength = 2;
        var previousDifference = normalized[1] - normalized[0];

        for (var index = 2; index < normalized.Length; index++)
        {
            var difference = normalized[index] - normalized[index - 1];
            if (difference == previousDifference && Math.Abs(difference) == 1)
            {
                sequenceLength++;
                continue;
            }

            if (sequenceLength >= 3)
                penalty += (sequenceLength - 2) * 5;

            sequenceLength = 2;
            previousDifference = difference;
        }

        if (sequenceLength >= 3)
            penalty += (sequenceLength - 2) * 5;

        return penalty;
    }

    private static double CalculateKeyboardPenalty(string normalized)
    {
        var longestMatch = 0;

        foreach (var row in KeyboardRows)
        {
            longestMatch = Math.Max(longestMatch, FindLongestContainedRun(normalized, row));
            longestMatch = Math.Max(longestMatch, FindLongestContainedRun(normalized, Reverse(row)));
        }

        return longestMatch >= 4 ? (longestMatch - 3) * 6 : 0;
    }

    private static int FindLongestContainedRun(string value, string row)
    {
        var longest = 0;

        for (var start = 0; start < row.Length; start++)
        {
            for (var length = 4; start + length <= row.Length; length++)
            {
                if (value.Contains(row.Substring(start, length), StringComparison.Ordinal))
                    longest = Math.Max(longest, length);
            }
        }

        return longest;
    }

    private static double CalculateLowVarietyPenalty(IReadOnlyList<Rune> runes)
    {
        var distinctCount = runes.Distinct().Count();
        if (distinctCount <= 1)
            return runes.Count * 5;
        if (distinctCount == 2)
            return runes.Count * 3;

        var ratio = distinctCount / (double)runes.Count;
        return ratio < 0.35 ? (0.35 - ratio) * runes.Count * 6 : 0;
    }

    private static double ApplyRepeatedPatternLimit(double estimatedBits, IReadOnlyList<Rune> runes)
    {
        for (var unitLength = 1; unitLength <= runes.Count / 2; unitLength++)
        {
            if (runes.Count % unitLength != 0)
                continue;

            var repeats = true;
            for (var index = unitLength; index < runes.Count; index++)
            {
                if (runes[index] == runes[index % unitLength])
                    continue;

                repeats = false;
                break;
            }

            if (!repeats)
                continue;

            var unit = runes.Take(unitLength).ToArray();
            var repeatCount = runes.Count / unitLength;
            if (unitLength == 1)
                return Math.Min(estimatedBits, 8);

            var repeatedEstimate = CalculateBaseEntropyBits(unit) + Math.Log2(repeatCount + 1) + 6;
            return Math.Min(estimatedBits, repeatedEstimate);
        }

        return estimatedBits;
    }

    private static bool ContainsCommonPassword(string normalized)
    {
        if (CommonPasswords.Contains(normalized))
            return true;

        foreach (var commonPassword in CommonPasswords)
        {
            if (commonPassword.Length < 6)
                continue;

            if (normalized.StartsWith(commonPassword, StringComparison.Ordinal) &&
                IsPredictableAffix(normalized[commonPassword.Length..]))
            {
                return true;
            }

            if (normalized.EndsWith(commonPassword, StringComparison.Ordinal) &&
                IsPredictableAffix(normalized[..^commonPassword.Length]))
            {
                return true;
            }
        }

        return false;
    }


    private static bool IsPredictableAffix(string value)
    {
        if (value.Length <= 4)
            return true;

        if (value.All(char.IsDigit))
            return true;

        if (value.Distinct().Count() == 1)
            return true;

        return CalculateSequencePenalty(value) >= Math.Max(5, (value.Length - 2) * 4);
    }

    private static bool ContainsRelatedTerm(string normalizedPassword, IEnumerable<string?>? relatedTerms)
    {
        if (relatedTerms is null || normalizedPassword.Length < 3)
            return false;

        foreach (var relatedTerm in relatedTerms)
        {
            if (string.IsNullOrWhiteSpace(relatedTerm))
                continue;

            var candidate = relatedTerm;
            var atIndex = candidate.IndexOf('@');
            if (atIndex > 0)
                candidate = candidate[..atIndex];

            foreach (var part in RelatedTermSeparator.Split(candidate))
            {
                var normalizedTerm = Normalize(part, convertLeet: false);
                if (normalizedTerm.Length < 3)
                    continue;

                if (normalizedPassword.Contains(normalizedTerm, StringComparison.Ordinal))
                    return true;
            }
        }

        return false;
    }

    private static int MapEntropyToScore(double estimatedBits) => estimatedBits switch
    {
        < 12 => 1,
        < 20 => 2,
        < 28 => 3,
        < 36 => 4,
        < 48 => 5,
        < 60 => 6,
        < 72 => 7,
        < 86 => 8,
        < 104 => 9,
        _ => 10
    };

    private static int ApplyLengthLimit(int score, int length) => length switch
    {
        <= 4 => Math.Min(score, 1),
        <= 6 => Math.Min(score, 2),
        <= 7 => Math.Min(score, 3),
        <= 9 => Math.Min(score, 5),
        <= 11 => Math.Min(score, 7),
        <= 14 => Math.Min(score, 9),
        _ => score
    };

    private static string Normalize(string value, bool convertLeet)
    {
        var builder = new StringBuilder(value.Length);
        var decomposed = value.Normalize(NormalizationForm.FormD);

        for (var index = 0; index < decomposed.Length; index++)
        {
            var character = decomposed[index];
            if (CharUnicodeInfo.GetUnicodeCategory(character) == UnicodeCategory.NonSpacingMark)
                continue;

            var lowercase = char.ToLowerInvariant(character);
            if (convertLeet && ShouldConvertLeet(decomposed, index))
            {
                var replacement = GetLeetReplacement(lowercase);
                if (replacement != '\0')
                {
                    builder.Append(replacement);
                    continue;
                }
            }

            if (char.IsLetterOrDigit(lowercase))
                builder.Append(lowercase);
        }

        return builder.ToString();
    }

    private static bool ShouldConvertLeet(string value, int index)
    {
        var character = char.ToLowerInvariant(value[index]);
        if (GetLeetReplacement(character) == '\0')
            return false;

        var hasLetterBefore = index > 0 && char.IsLetter(value[index - 1]);
        var hasLetterAfter = index + 1 < value.Length && char.IsLetter(value[index + 1]);
        return hasLetterBefore || hasLetterAfter;
    }

    private static char GetLeetReplacement(char character) => character switch
    {
        '@' => 'a',
        '4' => 'a',
        '3' => 'e',
        '1' => 'i',
        '!' => 'i',
        '0' => 'o',
        '$' => 's',
        '5' => 's',
        '7' => 't',
        '+' => 't',
        _ => '\0'
    };

    private static string Reverse(string value)
    {
        var characters = value.ToCharArray();
        Array.Reverse(characters);
        return new string(characters);
    }
}
