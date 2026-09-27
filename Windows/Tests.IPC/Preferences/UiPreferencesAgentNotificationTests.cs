using Microsoft.VisualStudio.TestTools.UnitTesting;
using PasswordManagerLocal.Common.Contracts.Preferences;
using PasswordManagerLocal.Common.Frontend.Services;
using PasswordManagerLocal.Windows.Tests.IPC.Infrastructure;

namespace PasswordManagerLocal.Windows.Tests.IPC.Preferences;

[TestClass]
public sealed class UiPreferencesAgentNotificationTests
{
    [TestMethod]
    public void LanguageIsPersistedBeforeAgentReloadIsSent()
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
        var service = new UiPreferencesService(store, notifier);
        operations.Clear();

        service.CurrentLanguage = AppLanguage.Hungarian;

        CollectionAssert.AreEqual(new[] { "write", "notify" }, operations);
        Assert.AreEqual(AppLanguage.Hungarian, store.Current.Language);
        Assert.AreEqual(1, notifier.NotifyCount);
    }

    [TestMethod]
    public void ThemeOnlyAndSameLanguageChangesDoNotNotifyAgent()
    {
        var store = new MutableApplicationPreferencesStore(AppLanguage.English, AppThemeMode.Light);
        var notifier = new FakeApplicationPreferencesChangeNotifier();
        var service = new UiPreferencesService(store, notifier);

        service.CurrentLanguage = AppLanguage.English;
        service.CurrentThemeMode = AppThemeMode.Dark;

        Assert.AreEqual(0, notifier.NotifyCount);
        Assert.AreEqual(1, store.WriteCount);
        Assert.AreEqual(AppThemeMode.Dark, store.Current.Theme);
    }

    [TestMethod]
    public void PersistenceFailureDoesNotNotifyOrChangeInMemoryLanguage()
    {
        var store = new MutableApplicationPreferencesStore(AppLanguage.English)
        {
            WriteFailure = new IOException("write failed")
        };
        var notifier = new FakeApplicationPreferencesChangeNotifier();
        var service = new UiPreferencesService(store, notifier);

        Assert.ThrowsExactly<IOException>(() => service.CurrentLanguage = AppLanguage.Hungarian);

        Assert.AreEqual(AppLanguage.English, service.CurrentLanguage);
        Assert.AreEqual(AppLanguage.English, store.Current.Language);
        Assert.AreEqual(0, notifier.NotifyCount);
    }

    [TestMethod]
    public void NotificationFailureDoesNotRollbackPersistedPreference()
    {
        var store = new MutableApplicationPreferencesStore(AppLanguage.English);
        var notifier = new FakeApplicationPreferencesChangeNotifier
        {
            Failure = new IOException("IPC unavailable")
        };
        var service = new UiPreferencesService(store, notifier);

        service.CurrentLanguage = AppLanguage.Hungarian;

        Assert.AreEqual(AppLanguage.Hungarian, service.CurrentLanguage);
        Assert.AreEqual(AppLanguage.Hungarian, store.Current.Language);
        Assert.AreEqual(1, notifier.NotifyCount);
    }
}
