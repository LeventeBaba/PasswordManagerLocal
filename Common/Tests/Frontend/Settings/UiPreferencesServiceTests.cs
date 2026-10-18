using Microsoft.VisualStudio.TestTools.UnitTesting;
using PasswordManagerLocal.Common.Contracts.Preferences;
using PasswordManagerLocal.Common.Frontend.Services;
using PasswordManagerLocal.Common.Tests.Fakes;

namespace PasswordManagerLocal.Common.Tests.Frontend.Settings;

[TestClass]
public sealed class UiPreferencesServiceTests
{
    [TestMethod]
    public async Task LanguageAndThemeChangesPersistThroughSharedStore()
    {
        var store = new InMemoryApplicationPreferencesStore(new ApplicationPreferences
        {
            SchemaVersion = ApplicationPreferences.CurrentSchemaVersion,
            Language = AppLanguage.English,
            Theme = AppThemeMode.Dark
        });
        await using var service = await TestUiPreferencesFactory.CreateAsync(store: store);

        Assert.AreEqual(
            UiPreferenceUpdateStatus.Applied,
            (await service.SetLanguageAsync(AppLanguage.Hungarian)).Status);
        Assert.AreEqual(
            UiPreferenceUpdateStatus.Applied,
            (await service.SetThemeModeAsync(AppThemeMode.Light)).Status);
        await using var reloaded = await TestUiPreferencesFactory.CreateAsync(store: store);

        Assert.AreEqual(AppLanguage.Hungarian, reloaded.CurrentLanguage);
        Assert.AreEqual(AppThemeMode.Light, reloaded.CurrentThemeMode);
        Assert.AreEqual(2, store.WriteCount);
    }
}
