namespace PasswordManagerLocal.Windows.Agent.Localization;

/// <summary>
/// Build/test-time validation for equivalence between independently loaded Agent resources.
/// It deliberately does not cache resources in the Agent process.
/// </summary>
public static class AgentLocalizationResourceSetValidator
{
    public static void ValidateEquivalent(
        IReadOnlyDictionary<string, string> reference,
        IReadOnlyDictionary<string, string> candidate)
    {
        ArgumentNullException.ThrowIfNull(reference);
        ArgumentNullException.ThrowIfNull(candidate);
        AgentLocalizationResourceParser.ValidateDictionary(reference);
        AgentLocalizationResourceParser.ValidateDictionary(candidate);

        foreach (var key in AgentLocalizationKeys.All)
        {
            var referencePlaceholders = AgentLocalizationResourceParser.GetPlaceholderSet(reference[key]);
            var candidatePlaceholders = AgentLocalizationResourceParser.GetPlaceholderSet(candidate[key]);
            if (!referencePlaceholders.SetEquals(candidatePlaceholders))
            {
                throw new InvalidDataException(
                    $"Agent localization placeholders do not match for key: {key}");
            }

            if (CountNewlines(reference[key]) != CountNewlines(candidate[key]))
            {
                throw new InvalidDataException(
                    $"Agent localization newline usage does not match for key: {key}");
            }
        }
    }

    private static int CountNewlines(string value)
    {
        var count = 0;
        for (var index = 0; index < value.Length; index++)
        {
            if (value[index] == '\r')
            {
                count++;
                if (index + 1 < value.Length && value[index + 1] == '\n')
                    index++;
            }
            else if (value[index] == '\n')
            {
                count++;
            }
        }
        return count;
    }
}
