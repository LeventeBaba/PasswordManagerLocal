using System.Security.Cryptography;

namespace PasswordManagerLocal.Common.Frontend.Security;

public sealed class MaximumStrengthPasswordGenerator
{
    private const int MinimumLength = 16;
    private const int AttemptsPerLength = 256;
    private const string LowercaseCharacters = "abcdefghijkmnopqrstuvwxyz";
    private const string UppercaseCharacters = "ABCDEFGHJKLMNPQRSTUVWXYZ";
    private const string DigitCharacters = "23456789";
    private const string SymbolCharacters = "!#$%&()*+,-./:;<=>?@[]^_{|}~";
    private const string AllCharacters = LowercaseCharacters + UppercaseCharacters + DigitCharacters + SymbolCharacters;

    private readonly PasswordStrengthEstimator _strengthEstimator;

    public MaximumStrengthPasswordGenerator(PasswordStrengthEstimator strengthEstimator)
    {
        _strengthEstimator = strengthEstimator;
    }

    public string Generate(IEnumerable<string?>? relatedTerms = null)
    {
        for (var length = MinimumLength; ; length++)
        {
            for (var attempt = 0; attempt < AttemptsPerLength; attempt++)
            {
                var password = GenerateCandidate(length);
                if (_strengthEstimator.Evaluate(password, relatedTerms).Score == 10)
                    return password;
            }
        }
    }

    private static string GenerateCandidate(int length)
    {
        var characters = new char[length];
        characters[0] = SelectCharacter(LowercaseCharacters);
        characters[1] = SelectCharacter(UppercaseCharacters);
        characters[2] = SelectCharacter(DigitCharacters);
        characters[3] = SelectCharacter(SymbolCharacters);

        for (var index = 4; index < characters.Length; index++)
            characters[index] = SelectCharacter(AllCharacters);

        Shuffle(characters);
        var password = new string(characters);
        Array.Clear(characters);
        return password;
    }

    private static char SelectCharacter(string characters) =>
        characters[RandomNumberGenerator.GetInt32(characters.Length)];

    private static void Shuffle(char[] characters)
    {
        for (var index = characters.Length - 1; index > 0; index--)
        {
            var swapIndex = RandomNumberGenerator.GetInt32(index + 1);
            (characters[index], characters[swapIndex]) = (characters[swapIndex], characters[index]);
        }
    }
}
