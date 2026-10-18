using System.Text;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using PasswordManagerLocal.Common.Contracts.Preferences;
using PasswordManagerLocal.Common.Frontend.Localization;
using PasswordManagerLocal.Common.Frontend.Services;
using PasswordManagerLocal.Common.Frontend.ViewModels;
using PasswordManagerLocal.Common.Preferences;
using PasswordManagerLocal.Common.Tests.Fakes;

namespace PasswordManagerLocal.Common.Tests.Frontend.Settings;

[TestClass]
public sealed class UiPreferencesPhase18Tests
{
    [TestMethod]
    public async Task PreferenceWritesAreSerializedAndPreserveEarlierFields()
    {
        var firstWriteEntered = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseFirstWrite = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var store = new CoordinatedPreferencesStore(DefaultPreferences())
        {
            BeforeWrite = async (writeNumber, _, cancellationToken) =>
            {
                if (writeNumber != 1)
                    return;
                firstWriteEntered.TrySetResult();
                await releaseFirstWrite.Task.WaitAsync(cancellationToken);
            }
        };
        await using var service = await CreateServiceAsync(store);

        var themeChange = service.SetThemeModeAsync(AppThemeMode.Light);
        await firstWriteEntered.Task.WaitAsync(TimeSpan.FromSeconds(2));
        var animationChange = service.SetInterfaceAnimationsEnabledAsync(false);
        releaseFirstWrite.TrySetResult();

        Assert.AreEqual(UiPreferenceUpdateStatus.Applied, (await themeChange).Status);
        Assert.AreEqual(UiPreferenceUpdateStatus.Applied, (await animationChange).Status);
        Assert.AreEqual(2, store.Writes.Count);
        Assert.AreEqual(AppThemeMode.Light, store.Writes[0].Theme);
        Assert.IsTrue(store.Writes[0].InterfaceAnimationsEnabled);
        Assert.AreEqual(AppThemeMode.Light, store.Writes[1].Theme);
        Assert.IsFalse(store.Writes[1].InterfaceAnimationsEnabled);
        Assert.AreEqual(AppThemeMode.Light, service.CurrentThemeMode);
        Assert.IsFalse(service.InterfaceAnimationsEnabled);
    }

    [TestMethod]
    public async Task CanceledPreferenceWriteThatIgnoresCancellationIsRolledBack()
    {
        using var cancellation = new CancellationTokenSource();
        var store = new CoordinatedPreferencesStore(DefaultPreferences())
        {
            HonorCancellation = false,
            BeforeWrite = (writeNumber, _, _) =>
            {
                if (writeNumber == 1)
                    cancellation.Cancel();
                return Task.CompletedTask;
            }
        };
        await using var service = await CreateServiceAsync(store);

        var result = await service.SetThemeModeAsync(AppThemeMode.Light, cancellation.Token);

        Assert.AreEqual(UiPreferenceUpdateStatus.Canceled, result.Status);
        Assert.IsFalse(result.PersistenceStateUncertain);
        Assert.AreEqual(2, store.Writes.Count);
        Assert.AreEqual(AppThemeMode.Light, store.Writes[0].Theme);
        Assert.AreEqual(AppThemeMode.Dark, store.Writes[1].Theme);
        Assert.AreEqual(AppThemeMode.Dark, service.CurrentThemeMode);
        Assert.AreEqual(AppThemeMode.Dark, store.Current.Theme);
    }

    [TestMethod]
    public async Task PersistenceFailureKeepsLanguageSnapshotAndPreferenceCoherent()
    {
        var notifier = new RecordingNotifier();
        var store = new CoordinatedPreferencesStore(DefaultPreferences())
        {
            Failure = new IOException("simulated write failure")
        };
        await using var service = await CreateServiceAsync(store, notifier);

        var result = await service.SetLanguageAsync(AppLanguage.Hungarian);

        Assert.AreEqual(UiPreferenceUpdateStatus.Failed, result.Status);
        Assert.AreEqual(AppLanguage.English, service.CurrentLanguage);
        Assert.AreEqual(AppLanguage.English, service.Localization.ActiveLanguage);
        Assert.AreEqual("English", service.GetString("Test_Label"));
        Assert.AreEqual(AppLanguage.English, (await store.ReadAsync()).Language);
        Assert.AreEqual(0, notifier.Count);
    }

    [TestMethod]
    public async Task SupersededWriteThatIgnoresCancellationIsRolledBack()
    {
        var firstWriteEntered = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseFirstWrite = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var store = new CoordinatedPreferencesStore(DefaultPreferences())
        {
            HonorCancellation = false,
            BeforeWrite = async (writeNumber, _, _) =>
            {
                if (writeNumber != 1)
                    return;
                firstWriteEntered.TrySetResult();
                await releaseFirstWrite.Task;
            }
        };
        await using var service = await CreateServiceAsync(store);

        var older = service.SetLanguageAsync(AppLanguage.Hungarian);
        await firstWriteEntered.Task.WaitAsync(TimeSpan.FromSeconds(2));
        var newer = service.SetLanguageAsync(AppLanguage.English);
        Assert.AreEqual(UiPreferenceUpdateStatus.Unchanged, (await newer).Status);
        releaseFirstWrite.TrySetResult();

        var olderResult = await older;
        Assert.AreEqual(UiPreferenceUpdateStatus.Superseded, olderResult.Status);
        Assert.IsFalse(olderResult.PersistenceStateUncertain);
        Assert.AreEqual(2, store.Writes.Count);
        Assert.AreEqual(AppLanguage.Hungarian, store.Writes[0].Language);
        Assert.AreEqual(AppLanguage.English, store.Writes[1].Language);
        Assert.AreEqual(AppLanguage.English, service.CurrentLanguage);
        Assert.AreEqual(AppLanguage.English, service.Localization.ActiveLanguage);
    }

    [TestMethod]
    public async Task SessionDisposalRollsBackWriteThatCompletesAfterCancellation()
    {
        var firstWriteEntered = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseFirstWrite = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var store = new CoordinatedPreferencesStore(DefaultPreferences())
        {
            HonorCancellation = false,
            BeforeWrite = async (writeNumber, _, _) =>
            {
                if (writeNumber != 1)
                    return;
                firstWriteEntered.TrySetResult();
                await releaseFirstWrite.Task;
            }
        };
        var service = await CreateServiceAsync(store);

        var change = service.SetLanguageAsync(AppLanguage.Hungarian);
        await firstWriteEntered.Task.WaitAsync(TimeSpan.FromSeconds(2));
        service.Deactivate();
        releaseFirstWrite.TrySetResult();

        var result = await change;
        await service.DisposeAsync();

        Assert.AreEqual(UiPreferenceUpdateStatus.Disposed, result.Status);
        Assert.IsFalse(result.PersistenceStateUncertain);
        Assert.AreEqual(2, store.Writes.Count);
        Assert.AreEqual(AppLanguage.Hungarian, store.Writes[0].Language);
        Assert.AreEqual(AppLanguage.English, store.Writes[1].Language);
        Assert.AreEqual(AppLanguage.English, store.Current.Language);
    }

    [TestMethod]
    public async Task AgentNotificationRunsOnlyAfterPersistenceAndLocalizationCommit()
    {
        var store = new CoordinatedPreferencesStore(DefaultPreferences());
        UiPreferencesService? serviceForNotification = null;
        AppLanguage? persistedAtNotification = null;
        AppLanguage? activeAtNotification = null;
        var notifier = new RecordingNotifier(() =>
        {
            persistedAtNotification = store.Current.Language;
            activeAtNotification = serviceForNotification?.Localization.ActiveLanguage;
        });
        await using var service = await CreateServiceAsync(store, notifier);
        serviceForNotification = service;

        var result = await service.SetLanguageAsync(AppLanguage.Hungarian);

        Assert.AreEqual(UiPreferenceUpdateStatus.Applied, result.Status);
        Assert.AreEqual(1, notifier.Count);
        Assert.AreEqual(AppLanguage.Hungarian, persistedAtNotification);
        Assert.AreEqual(AppLanguage.Hungarian, activeAtNotification);
    }

    [TestMethod]
    public async Task CloseAndReopenReleasesOldSnapshotAndLoadsPersistedLanguage()
    {
        var store = new CoordinatedPreferencesStore(DefaultPreferences());
        var first = await CreateServiceAsync(store);
        var retiredLocalization = first.Localization;
        Assert.AreEqual(
            UiPreferenceUpdateStatus.Applied,
            (await first.SetLanguageAsync(AppLanguage.Hungarian)).Status);

        await first.DisposeAsync();

        Assert.IsFalse(retiredLocalization.HasActiveSnapshot);
        Assert.AreEqual(0, retiredLocalization.ActiveTranslationCount);
        await using var reopened = await CreateServiceAsync(store);
        Assert.AreEqual(AppLanguage.Hungarian, reopened.CurrentLanguage);
        Assert.AreEqual(AppLanguage.Hungarian, reopened.Localization.ActiveLanguage);
        Assert.AreEqual("Magyar", reopened.GetString("Test_Label"));
    }

    [TestMethod]
    public async Task LanguageSwitchRefreshesTextWithoutDiscardingEditorState()
    {
        var store = new CoordinatedPreferencesStore(DefaultPreferences());
        await using var service = await CreateServiceAsync(store);
        using var editor = new DraftEditorViewModel(service)
        {
            Draft = "unsaved secret draft",
            SearchText = "needle",
            Selection = 7
        };
        editor.ShowLocalizedStatus();
        Assert.AreEqual("English", editor.Label);
        Assert.AreEqual("English", editor.StatusMessage);

        var result = await service.SetLanguageAsync(AppLanguage.Hungarian);

        Assert.AreEqual(UiPreferenceUpdateStatus.Applied, result.Status);
        Assert.AreEqual("Magyar", editor.Label);
        Assert.AreEqual("Magyar", editor.StatusMessage);
        Assert.AreEqual("unsaved secret draft", editor.Draft);
        Assert.AreEqual("needle", editor.SearchText);
        Assert.AreEqual(7, editor.Selection);
    }

    private static Task<UiPreferencesService> CreateServiceAsync(
        IApplicationPreferencesStore store,
        IApplicationPreferencesChangeNotifier? notifier = null) =>
        UiPreferencesService.CreateAsync(
            store,
            notifier,
            localizationResourceLoader: new TestResourceLoader());

    private static ApplicationPreferences DefaultPreferences() =>
        new()
        {
            SchemaVersion = ApplicationPreferences.CurrentSchemaVersion,
            Language = AppLanguage.English,
            Theme = AppThemeMode.Dark,
            InterfaceAnimationsEnabled = true
        };

    private sealed class TestResourceLoader : IFrontendLocalizationResourceLoader
    {
        public ValueTask<Stream> OpenAsync(
            AppLanguage language,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var value = language switch
            {
                AppLanguage.English => "English",
                AppLanguage.Hungarian => "Magyar",
                _ => throw new ArgumentOutOfRangeException(nameof(language), language, null)
            };
            return ValueTask.FromResult<Stream>(new MemoryStream(
                Encoding.UTF8.GetBytes($"{{\"Test_Label\":\"{value}\"}}"),
                writable: false));
        }
    }

    private sealed class CoordinatedPreferencesStore(ApplicationPreferences initial) :
        IApplicationPreferencesStore
    {
        private readonly object _gate = new();
        private ApplicationPreferences _current = initial;
        private int _writeCount;

        public Func<int, ApplicationPreferences, CancellationToken, Task>? BeforeWrite { get; init; }
        public Exception? Failure { get; init; }
        public bool HonorCancellation { get; init; } = true;
        public List<ApplicationPreferences> Writes { get; } = [];

        public ApplicationPreferences Current
        {
            get { lock (_gate) return _current; }
        }

        public Task<ApplicationPreferences> ReadAsync(
            CancellationToken cancellationToken = default)
        {
            if (HonorCancellation)
                cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(Current);
        }

        public async Task WriteAsync(
            ApplicationPreferences preferences,
            CancellationToken cancellationToken = default)
        {
            var writeNumber = Interlocked.Increment(ref _writeCount);
            if (BeforeWrite is not null)
                await BeforeWrite(writeNumber, preferences, cancellationToken);
            if (HonorCancellation)
                cancellationToken.ThrowIfCancellationRequested();
            if (Failure is not null)
                throw Failure;
            lock (_gate)
            {
                _current = preferences;
                Writes.Add(preferences);
            }
        }
    }

    private sealed class RecordingNotifier(Action? callback = null) :
        IApplicationPreferencesChangeNotifier
    {
        public int Count { get; private set; }

        public Task NotifyLanguagePersistedAsync(
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Count++;
            callback?.Invoke();
            return Task.CompletedTask;
        }
    }

    private sealed class DraftEditorViewModel(UiPreferencesService preferences) :
        ViewModelBase(preferences)
    {
        private string _draft = string.Empty;
        private string _searchText = string.Empty;
        private int _selection;

        public string Label => GetTranslation("Test_Label");

        public string Draft
        {
            get => _draft;
            set => _draft = value;
        }

        public string SearchText
        {
            get => _searchText;
            set => _searchText = value;
        }

        public int Selection
        {
            get => _selection;
            set => _selection = value;
        }

        public void ShowLocalizedStatus() => ShowLocalizedInformationMessage("Test_Label");

        protected override void OnLanguageChanged() =>
            RaisePropertiesChanged([nameof(Label)]);
    }
}
