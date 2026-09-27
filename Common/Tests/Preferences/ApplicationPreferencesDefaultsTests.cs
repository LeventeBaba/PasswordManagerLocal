using Microsoft.VisualStudio.TestTools.UnitTesting;
using PasswordManagerLocal.Common.Contracts.Preferences;
using PasswordManagerLocal.Common.Preferences;
using System.Globalization;

namespace PasswordManagerLocal.Common.Tests.Preferences;

[TestClass]
[DoNotParallelize]
public sealed class ApplicationPreferencesDefaultsTests
{
    [DataTestMethod]
    [DataRow("en-US", AppLanguage.English)]
    [DataRow("hu-HU", AppLanguage.Hungarian)]
    public void DefaultsFollowCultureAndAlwaysUseDarkTheme(
        string cultureName,
        AppLanguage expectedLanguage)
    {
        var originalCulture = CultureInfo.CurrentCulture;
        var originalUiCulture = CultureInfo.CurrentUICulture;
        try
        {
            var culture = CultureInfo.GetCultureInfo(cultureName);
            CultureInfo.CurrentCulture = culture;
            CultureInfo.CurrentUICulture = culture;

            var defaults = ApplicationPreferencesDefaults.Create();

            Assert.AreEqual(ApplicationPreferences.CurrentSchemaVersion, defaults.SchemaVersion);
            Assert.AreEqual(expectedLanguage, defaults.Language);
            Assert.AreEqual(AppThemeMode.Dark, defaults.Theme);
        }
        finally
        {
            CultureInfo.CurrentCulture = originalCulture;
            CultureInfo.CurrentUICulture = originalUiCulture;
        }
    }
}
