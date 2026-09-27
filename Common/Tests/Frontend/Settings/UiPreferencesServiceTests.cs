using Microsoft.VisualStudio.TestTools.UnitTesting;
using PasswordManagerLocal.Common.Contracts.Preferences;
using PasswordManagerLocal.Common.Frontend.Services;
using PasswordManagerLocal.Common.Tests.Fakes;

namespace PasswordManagerLocal.Common.Tests.Frontend.Settings;

[TestClass]
public sealed class UiPreferencesServiceTests
{
    [TestMethod]
    public void LanguageAndThemeChangesPersistThroughSharedStore()
    {
        var store = new InMemoryApplicationPreferencesStore(new ApplicationPreferences
        {
            SchemaVersion = ApplicationPreferences.CurrentSchemaVersion,
            Language = AppLanguage.English,
            Theme = AppThemeMode.Dark
        });
        var service = new UiPreferencesService(store);

        service.CurrentLanguage = AppLanguage.Hungarian;
        service.CurrentThemeMode = AppThemeMode.Light;
        var reloaded = new UiPreferencesService(store);

        Assert.AreEqual(AppLanguage.Hungarian, reloaded.CurrentLanguage);
        Assert.AreEqual(AppThemeMode.Light, reloaded.CurrentThemeMode);
        Assert.AreEqual(2, store.WriteCount);
    }
}
