using Microsoft.VisualStudio.TestTools.UnitTesting;
using PasswordManagerLocal.Common.Contracts.Preferences;
using PasswordManagerLocal.Common.Frontend.Services;
using PasswordManagerLocal.Windows.Tests.IPC.Infrastructure;

namespace PasswordManagerLocal.Windows.Tests.IPC.Preferences;

[TestClass]
public sealed class UiPreferencesAgentNotificationTests
{
    [TestMethod]
    public async Task LanguageIsPersistedBeforeAgentReloadIsSent()
    {
        var operations = new List<string>();
        var store = new MutableApplicationPreferencesStore(AppLanguage.English)
        {
            OperationLog = operations
        };
        var notifier = new FakeApplicationPreferencesChangeNotifier
        {
            OperationLog = operations
        };
        await using var service = await CreateServiceAsync(store, notifier);
        operations.Clear();

        var result = await service.SetLanguageAsync(AppLanguage.Hungarian);

        CollectionAssert.AreEqual(new[] { "write", "notify" }, operations);
        Assert.AreEqual(UiPreferenceUpdateStatus.Applied, result.Status);
        Assert.AreEqual(AppLanguage.Hungarian, store.Current.Language);
        Assert.AreEqual(1, notifier.NotifyCount);
    }

    [TestMethod]
    public async Task ThemeOnlyAndSameLanguageChangesDoNotNotifyAgent()
    {
        var store = new MutableApplicationPreferencesStore(AppLanguage.English, AppThemeMode.Light);
        var notifier = new FakeApplicationPreferencesChangeNotifier();
        await using var service = await CreateServiceAsync(store, notifier);

        await service.SetLanguageAsync(AppLanguage.English);
        await service.SetThemeModeAsync(AppThemeMode.Dark);

        Assert.AreEqual(0, notifier.NotifyCount);
        Assert.AreEqual(1, store.WriteCount);
        Assert.AreEqual(AppThemeMode.Dark, store.Current.Theme);
    }

    [TestMethod]
    public async Task PersistenceFailureDoesNotNotifyOrChangeInMemoryLanguage()
    {
        var store = new MutableApplicationPreferencesStore(AppLanguage.English)
        {
            WriteFailure = new IOException("write failed")
        };
        var notifier = new FakeApplicationPreferencesChangeNotifier();
        await using var service = await CreateServiceAsync(store, notifier);

        var result = await service.SetLanguageAsync(AppLanguage.Hungarian);

        Assert.AreEqual(UiPreferenceUpdateStatus.Failed, result.Status);
        Assert.AreEqual(AppLanguage.English, service.CurrentLanguage);
        Assert.AreEqual(AppLanguage.English, store.Current.Language);
        Assert.AreEqual(0, notifier.NotifyCount);
    }

    [TestMethod]
    public async Task NotificationFailureDoesNotRollbackPersistedPreference()
    {
        var store = new MutableApplicationPreferencesStore(AppLanguage.English);
        var notifier = new FakeApplicationPreferencesChangeNotifier
        {
            Failure = new IOException("IPC unavailable")
        };
        await using var service = await CreateServiceAsync(store, notifier);

        var result = await service.SetLanguageAsync(AppLanguage.Hungarian);

        Assert.AreEqual(UiPreferenceUpdateStatus.Applied, result.Status);
        Assert.IsTrue(result.AgentNotificationFailed);
        Assert.AreEqual(AppLanguage.Hungarian, service.CurrentLanguage);
        Assert.AreEqual(AppLanguage.Hungarian, store.Current.Language);
        Assert.AreEqual(1, notifier.NotifyCount);
    }

    private static Task<UiPreferencesService> CreateServiceAsync(
        MutableApplicationPreferencesStore store,
        FakeApplicationPreferencesChangeNotifier notifier) =>
        UiPreferencesService.CreateAsync(
            store,
            notifier,
            localizationResourceLoader: new TestFrontendLocalizationResourceLoader());
}
