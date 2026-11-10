using PasswordManagerLocal.Common.Contracts.Preferences;

namespace PasswordManagerLocal.Common.Frontend.Information;

internal static class PrivacyPolicyLinks
{
    private const string Site = "https://github.com/LeventeBaba/PasswordManagerLocal/blob/Demo/Docs/Privacy/";
    private static readonly Uri English = new(Site + "PrivacyPolicy.en.md");
    private static readonly Uri Hungarian = new(Site + "PrivacyPolicy.hu.md");

    internal static Uri ForLanguage(AppLanguage language) =>
        language == AppLanguage.Hungarian ? Hungarian : English;
}
