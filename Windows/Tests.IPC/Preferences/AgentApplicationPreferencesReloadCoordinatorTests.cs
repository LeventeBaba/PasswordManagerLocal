using Microsoft.VisualStudio.TestTools.UnitTesting;
using PasswordManagerLocal.Common.Contracts.Preferences;
using PasswordManagerLocal.Windows.Agent.Localization;
using PasswordManagerLocal.Windows.Agent.Preferences;
using PasswordManagerLocal.Windows.Tests.IPC.Infrastructure;

namespace PasswordManagerLocal.Windows.Tests.IPC.Preferences;

[TestClass]
public sealed class AgentApplicationPreferencesReloadCoordinatorTests
{
    [TestMethod]
    public async Task ExplicitReloadRereadsAuthoritativeStoreAndUpdatesTrayText()
    {
        var store = new MutableApplicationPreferencesStore(AppLanguage.Hungarian);
        var reader = new WindowsAgentApplicationPreferencesReader(store);
        var loader = CreateLoader();
        var localizer = await AgentLocalizer.CreateAsync(AppLanguage.English, loader);
        var tray = new FakeTrayIconController();
        var stampProvider = new FakeApplicationPreferencesFileStampProvider
        {
            Stamp = Stamp(2)
        };
        await using var coordinator = new AgentApplicationPreferencesReloadCoordinator(
            reader,
            localizer,
            tray,
            stampProvider,
            Stamp(1));

        var result = await coordinator.ReloadAsync();

        Assert.IsTrue(result);
        Assert.AreEqual(1, store.ReadCount);
        Assert.AreEqual(AppLanguage.Hungarian, localizer.CurrentLanguage);
        Assert.AreEqual("hu-open", tray.LastText?.OpenLabel);
        Assert.AreEqual("hu-exit", tray.LastText?.ExitLabel);
        Assert.AreEqual(1, tray.UpdateTextCount);
    }

    [TestMethod]
    public async Task ConcurrentReloadsResolveToLatestSavedLanguage()
    {
        var store = new MutableApplicationPreferencesStore(AppLanguage.Hungarian);
        var firstReadEntered = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseFirstRead = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var readNumber = 0;
        store.ReadOverride = async cancellationToken =>
        {
            var captured = store.Current;
            if (Interlocked.Increment(ref readNumber) == 1)
            {
                firstReadEntered.TrySetResult();
                await releaseFirstRead.Task.WaitAsync(cancellationToken);
            }
            return captured;
        };
        var reader = new WindowsAgentApplicationPreferencesReader(store);
        var localizer = await AgentLocalizer.CreateAsync(AppLanguage.English, CreateLoader());
        var tray = new FakeTrayIconController();
        var stampProvider = new FakeApplicationPreferencesFileStampProvider { Stamp = Stamp(1) };
        await using var coordinator = new AgentApplicationPreferencesReloadCoordinator(
            reader, localizer, tray, stampProvider, Stamp(1));

        var first = coordinator.ReloadAsync();
        await firstReadEntered.Task;
        store.Current = new ApplicationPreferences
        {
            Language = AppLanguage.English,
            Theme = AppThemeMode.Dark
        };
        var second = coordinator.ReloadAsync();
        releaseFirstRead.TrySetResult();

        Assert.IsTrue(await first);
        Assert.IsTrue(await second);
        Assert.AreEqual(2, store.ReadCount);
        Assert.AreEqual(AppLanguage.English, localizer.CurrentLanguage);
        Assert.IsNull(tray.LastText);
        Assert.AreEqual(0, tray.UpdateTextCount);
    }


    [TestMethod]
    public async Task SameLanguageStampChangeDoesNotReloadResourceOrTrayText()
    {
        var store = new MutableApplicationPreferencesStore(
            AppLanguage.English,
            AppThemeMode.Dark);
        var loader = CreateLoader();
        var localizer = await AgentLocalizer.CreateAsync(AppLanguage.English, loader);
        var tray = new FakeTrayIconController();
        var stampProvider = new FakeApplicationPreferencesFileStampProvider { Stamp = Stamp(2) };
        await using var coordinator = new AgentApplicationPreferencesReloadCoordinator(
            new WindowsAgentApplicationPreferencesReader(store),
            localizer,
            tray,
            stampProvider,
            Stamp(1));
        var opensAfterStartup = loader.OpenCount;

        Assert.IsTrue(await coordinator.ReloadAsync());

        Assert.AreEqual(opensAfterStartup, loader.OpenCount);
        Assert.AreEqual(0, tray.UpdateTextCount);
        tray.RaiseContextMenuOpening();
        Assert.AreEqual(1, store.ReadCount);
    }

    [TestMethod]
    public async Task FailedReplacementKeepsPreviousLanguageAndCanRetry()
    {
        var store = new MutableApplicationPreferencesStore(AppLanguage.Hungarian);
        var loader = CreateLoader();
        loader.SetFailure(AppLanguage.Hungarian, new IOException("resource unavailable"));
        var localizer = await AgentLocalizer.CreateAsync(AppLanguage.English, loader);
        var tray = new FakeTrayIconController();
        var stampProvider = new FakeApplicationPreferencesFileStampProvider { Stamp = Stamp(2) };
        await using var coordinator = new AgentApplicationPreferencesReloadCoordinator(
            new WindowsAgentApplicationPreferencesReader(store),
            localizer,
            tray,
            stampProvider,
            Stamp(1));

        Assert.IsFalse(await coordinator.ReloadAsync());
        Assert.AreEqual(AppLanguage.English, localizer.CurrentLanguage);
        Assert.AreEqual(0, tray.UpdateTextCount);

        loader.SetResource(AppLanguage.Hungarian, CreateTranslations("hu", "hu-open", "hu-exit"));
        Assert.IsTrue(await coordinator.ReloadAsync());
        Assert.AreEqual(AppLanguage.Hungarian, localizer.CurrentLanguage);
        Assert.AreEqual(1, tray.UpdateTextCount);
    }

    [TestMethod]
    public async Task UnchangedStampDoesNotDeserializePreferences()
    {
        var store = new MutableApplicationPreferencesStore(AppLanguage.Hungarian);
        var localizer = await AgentLocalizer.CreateAsync(AppLanguage.English, CreateLoader());
        var tray = new FakeTrayIconController();
        var stampProvider = new FakeApplicationPreferencesFileStampProvider { Stamp = Stamp(1) };
        await using var coordinator = new AgentApplicationPreferencesReloadCoordinator(
            new WindowsAgentApplicationPreferencesReader(store),
            localizer,
            tray,
            stampProvider,
            Stamp(1));

        tray.RaiseContextMenuOpening();

        Assert.AreEqual(0, store.ReadCount);
        Assert.AreEqual(0, tray.UpdateTextCount);
    }

    [TestMethod]
    public async Task ChangedStampSchedulesRecoveryAndFailedRecoveryRetriesLater()
    {
        var store = new MutableApplicationPreferencesStore(AppLanguage.Hungarian);
        var loader = CreateLoader();
        loader.SetFailure(AppLanguage.Hungarian, new IOException("first attempt fails"));
        var localizer = await AgentLocalizer.CreateAsync(AppLanguage.English, loader);
        var tray = new FakeTrayIconController();
        var stampProvider = new FakeApplicationPreferencesFileStampProvider { Stamp = Stamp(2) };
        await using var coordinator = new AgentApplicationPreferencesReloadCoordinator(
            new WindowsAgentApplicationPreferencesReader(store),
            localizer,
            tray,
            stampProvider,
            Stamp(1));

        tray.RaiseContextMenuOpening();
        Assert.IsFalse(await coordinator.ReloadAsync());
        Assert.AreEqual(AppLanguage.English, localizer.CurrentLanguage);

        loader.SetResource(AppLanguage.Hungarian, CreateTranslations("hu", "hu-open", "hu-exit"));
        tray.RaiseContextMenuOpening();
        Assert.IsTrue(await coordinator.ReloadAsync());

        Assert.AreEqual(AppLanguage.Hungarian, localizer.CurrentLanguage);
        Assert.IsTrue(store.ReadCount >= 2);
        Assert.AreEqual("hu-open", tray.LastText?.OpenLabel);
    }


    [TestMethod]
    public async Task TrayUpdateFailureKeepsStampDirtySoMenuRecoveryCanRetry()
    {
        var store = new MutableApplicationPreferencesStore(AppLanguage.Hungarian);
        var localizer = await AgentLocalizer.CreateAsync(AppLanguage.English, CreateLoader());
        var tray = new FakeTrayIconController
        {
            UpdateTextFailure = new IOException("shell unavailable")
        };
        var stampProvider = new FakeApplicationPreferencesFileStampProvider { Stamp = Stamp(2) };
        await using var coordinator = new AgentApplicationPreferencesReloadCoordinator(
            new WindowsAgentApplicationPreferencesReader(store),
            localizer,
            tray,
            stampProvider,
            Stamp(1));

        Assert.IsFalse(await coordinator.ReloadAsync());
        Assert.AreEqual(AppLanguage.Hungarian, localizer.CurrentLanguage);

        tray.UpdateTextFailure = null;
        tray.RaiseContextMenuOpening();
        Assert.IsTrue(await coordinator.ReloadAsync());
        Assert.AreEqual("hu-open", tray.LastText?.OpenLabel);
        Assert.IsTrue(tray.UpdateTextCount >= 2);
    }

    [TestMethod]
    public async Task HiddenTrayStillReceivesLatestLocalizationAndShutdownRejectsWork()
    {
        var store = new MutableApplicationPreferencesStore(AppLanguage.Hungarian);
        var localizer = await AgentLocalizer.CreateAsync(AppLanguage.English, CreateLoader());
        var tray = new FakeTrayIconController();
        await tray.SetVisibleAsync(false);
        var coordinator = new AgentApplicationPreferencesReloadCoordinator(
            new WindowsAgentApplicationPreferencesReader(store),
            localizer,
            tray,
            new FakeApplicationPreferencesFileStampProvider { Stamp = Stamp(2) },
            Stamp(1));

        Assert.IsTrue(await coordinator.ReloadAsync());
        Assert.AreEqual("hu-open", tray.LastText?.OpenLabel);
        await tray.SetVisibleAsync(true);
        Assert.AreEqual("hu-open", tray.LastText?.OpenLabel);

        await coordinator.DisposeAsync();
        Assert.IsFalse(await coordinator.ReloadAsync());
        var reads = store.ReadCount;
        tray.RaiseContextMenuOpening();
        Assert.AreEqual(reads, store.ReadCount);
    }

    private static InMemoryAgentLocalizationResourceLoader CreateLoader()
    {
        var loader = new InMemoryAgentLocalizationResourceLoader();
        loader.SetResource(AppLanguage.English, CreateTranslations("en", "en-open", "en-exit"));
        loader.SetResource(AppLanguage.Hungarian, CreateTranslations("hu", "hu-open", "hu-exit"));
        return loader;
    }

    private static Dictionary<string, string> CreateTranslations(
        string prefix,
        string open,
        string exit)
    {
        var translations = InMemoryAgentLocalizationResourceLoader.CreateValidTranslations(prefix);
        translations[AgentLocalizationKeys.TrayOpen] = open;
        translations[AgentLocalizationKeys.TrayExit] = exit;
        translations[AgentLocalizationKeys.TrayTooltip] = $"{prefix}-tooltip";
        translations[AgentLocalizationKeys.TrayErrorTitle] = $"{prefix}-error";
        translations[AgentLocalizationKeys.StartupFailureTitle] = $"{prefix}-startup-title";
        translations[AgentLocalizationKeys.StartupFailureMessage] = $"{prefix}-startup-message";
        translations[AgentLocalizationKeys.ShutdownFailureTitle] = $"{prefix}-shutdown-title";
        translations[AgentLocalizationKeys.ShutdownFailureMessage] = $"{prefix}-shutdown-message";
        return translations;
    }

    private static ApplicationPreferencesFileStamp Stamp(long value) =>
        new(true, value, DateTime.UnixEpoch.AddSeconds(value));
}
