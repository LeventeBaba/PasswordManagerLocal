using PasswordManagerLocal.Common.Contracts.Preferences;
using System.Globalization;

namespace PasswordManagerLocal.Common.Preferences;

/// <summary>
/// Owns the single documented default definition for application preferences.
/// The language follows the current Hungarian culture/region when detectable and
/// otherwise falls back to English. The default frontend theme is Dark.
/// </summary>
public static class ApplicationPreferencesDefaults
{
    public const AppThemeMode DefaultTheme = AppThemeMode.Dark;

    public static ApplicationPreferences Create() =>
        new()
        {
            SchemaVersion = ApplicationPreferences.CurrentSchemaVersion,
            Language = DetectDefaultLanguage(),
            Theme = DefaultTheme
        };

    private static AppLanguage DetectDefaultLanguage()
    {
        try
        {
            if (IsHungarianCulture(CultureInfo.CurrentUICulture) ||
                IsHungarianCulture(CultureInfo.CurrentCulture))
            {
                return AppLanguage.Hungarian;
            }
        }
        catch (CultureNotFoundException)
        {
        }

        try
        {
            if (string.Equals(
                RegionInfo.CurrentRegion.TwoLetterISORegionName,
                "HU",
                StringComparison.OrdinalIgnoreCase))
            {
                return AppLanguage.Hungarian;
            }
        }
        catch (ArgumentException)
        {
        }

        return AppLanguage.English;
    }

    private static bool IsHungarianCulture(CultureInfo culture) =>
        string.Equals(culture.TwoLetterISOLanguageName, "hu", StringComparison.OrdinalIgnoreCase) ||
        string.Equals(culture.Name, "hu-HU", StringComparison.OrdinalIgnoreCase);
}
