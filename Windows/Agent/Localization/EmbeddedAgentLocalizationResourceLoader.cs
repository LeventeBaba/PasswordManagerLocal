using PasswordManagerLocal.Common.Contracts.Preferences;
using System.Reflection;

namespace PasswordManagerLocal.Windows.Agent.Localization;

public sealed class EmbeddedAgentLocalizationResourceLoader : IAgentLocalizationResourceLoader
{
    private readonly Assembly _assembly;

    public EmbeddedAgentLocalizationResourceLoader(Assembly? assembly = null) =>
        _assembly = assembly ?? typeof(EmbeddedAgentLocalizationResourceLoader).Assembly;

    public Stream Open(AppLanguage language)
    {
        var resourceName = GetResourceName(language);
        return _assembly.GetManifestResourceStream(resourceName)
            ?? throw new InvalidDataException($"The embedded Agent localization resource '{resourceName}' is missing.");
    }

    public static string GetFileName(AppLanguage language) => language switch
    {
        AppLanguage.English => "en_us.json",
        AppLanguage.Hungarian => "hu.json",
        _ => throw new ArgumentOutOfRangeException(nameof(language), language, "The Agent language is unsupported.")
    };

    internal static string GetResourceName(AppLanguage language) =>
        $"PasswordManagerLocal.Windows.Agent.Assets.Localization.{GetFileName(language)}";
}
