using PasswordManagerLocal.Common.Contracts.Preferences;

namespace PasswordManagerLocal.Common.Frontend.Information;

internal static class PrivacyPolicyLinks
{
    private const string Site = "https://leventebaba.github.io/PasswordManagerLocal/";
    private static readonly Uri English = new(Site + "privacy-policy.en.html");
    private static readonly Uri Hungarian = new(Site + "privacy-policy.hu.html");

    internal static Uri ForLanguage(AppLanguage language) =>
        language == AppLanguage.Hungarian ? Hungarian : English;
}
