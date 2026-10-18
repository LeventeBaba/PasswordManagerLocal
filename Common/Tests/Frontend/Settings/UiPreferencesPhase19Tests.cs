using Microsoft.VisualStudio.TestTools.UnitTesting;
using PasswordManagerLocal.Common.Contracts.Preferences;
using PasswordManagerLocal.Common.Frontend.Services;
using PasswordManagerLocal.Common.Frontend.Theming;
using PasswordManagerLocal.Common.Frontend.ViewModels;
using PasswordManagerLocal.Common.Preferences;
using PasswordManagerLocal.Common.Tests.Fakes;

namespace PasswordManagerLocal.Common.Tests.Frontend.Settings;

[TestClass]
public sealed class UiPreferencesPhase19Tests
{
    [TestMethod]
    public async Task ThemePersistenceFailurePreservesActiveThemeAndRollsBackStore()
    {
        var store = new ScriptedPreferencesStore(DefaultPreferences());
        store.FailWriteNumbers.Add(1);
        var host = new TestAppThemeResourceHost();
        await using var service = await TestUiPreferencesFactory.CreateAsync(
            store: store,
            themeManager: TestAppThemeFactory.CreateManager(host: host));

        var result = await service.SetThemeAsync(AppThemeId.Light);

        Assert.AreEqual(UiPreferenceUpdateStatus.Failed, result.Status);
        Assert.IsFalse(result.PersistenceStateUncertain);
        Assert.AreEqual(AppThemeId.Dark, service.CurrentThemeId);
        Assert.AreEqual(AppThemeId.Dark, host.ActiveThemeId);
        Assert.AreEqual(AppThemeId.Dark, store.Current.Theme);
        Assert.AreEqual(1, host.ReplacementCount);
        Assert.AreEqual(1, store.SuccessfulWrites.Count);
        Assert.AreEqual(AppThemeId.Dark, store.SuccessfulWrites[0].Theme);
    }

    [TestMethod]
    public async Task ApplyFailureRollsBackPersistenceAndKeepsCommittedResources()
    {
        var store = new ScriptedPreferencesStore(DefaultPreferences());
        var host = new TestAppThemeResourceHost();
        await using var service = await TestUiPreferencesFactory.CreateAsync(
            store: store,
            themeManager: TestAppThemeFactory.CreateManager(host: host));
        var active = host.ActiveResources;
        host.ReplaceFailure = new InvalidOperationException("simulated dispatcher-host failure");

        var result = await service.SetThemeAsync(AppThemeId.Light);

        Assert.AreEqual(UiPreferenceUpdateStatus.Failed, result.Status);
        Assert.IsFalse(result.PersistenceStateUncertain);
        Assert.AreEqual(AppThemeId.Dark, service.CurrentThemeId);
        Assert.AreSame(active, host.ActiveResources);
        Assert.AreEqual(AppThemeId.Dark, store.Current.Theme);
        CollectionAssert.AreEqual(
            new[] { AppThemeId.Light, AppThemeId.Dark },
            store.SuccessfulWrites.Select(preferences => preferences.Theme).ToArray());
    }

    [TestMethod]
    public async Task ConcurrentLanguageThemeAndAnimationWritesAreSerializedAndCoherent()
    {
        var firstWriteEntered = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseFirstWrite = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var store = new ScriptedPreferencesStore(DefaultPreferences())
        {
            BeforeWrite = async (writeNumber, _, cancellationToken) =>
            {
                if (writeNumber != 1)
                    return;
                firstWriteEntered.TrySetResult();
                await releaseFirstWrite.Task.WaitAsync(cancellationToken);
            }
        };
        await using var service = await TestUiPreferencesFactory.CreateAsync(store: store);

        var theme = service.SetThemeAsync(AppThemeId.Light);
        await firstWriteEntered.Task.WaitAsync(TimeSpan.FromSeconds(2));
        var language = service.SetLanguageAsync(AppLanguage.Hungarian);
        var animations = service.SetInterfaceAnimationsEnabledAsync(false);
        releaseFirstWrite.TrySetResult();
        var results = await Task.WhenAll(theme, language, animations);

        Assert.IsTrue(results.All(result => result.Status == UiPreferenceUpdateStatus.Applied));
        Assert.AreEqual(1, store.MaximumConcurrentWrites);
        Assert.AreEqual(3, store.SuccessfulWrites.Count);
        Assert.AreEqual(AppLanguage.Hungarian, store.Current.Language);
        Assert.AreEqual(AppThemeId.Light, store.Current.Theme);
        Assert.IsFalse(store.Current.InterfaceAnimationsEnabled);
        Assert.AreEqual(AppLanguage.Hungarian, service.CurrentLanguage);
        Assert.AreEqual(AppThemeId.Light, service.CurrentThemeId);
        Assert.IsFalse(service.InterfaceAnimationsEnabled);
        Assert.IsTrue(store.SuccessfulWrites.All(preferences =>
            preferences.SchemaVersion == ApplicationPreferences.CurrentSchemaVersion));
    }

    [TestMethod]
    public async Task RollbackCompletesBeforeFollowingPreferenceWrite()
    {
        var store = new ScriptedPreferencesStore(DefaultPreferences());
        await using var service = await TestUiPreferencesFactory.CreateAsync(store: store);
        Assert.AreEqual(
            UiPreferenceUpdateStatus.Applied,
            (await service.SetLanguageAsync(AppLanguage.Hungarian)).Status);
        store.FailWriteNumbers.Add(2);

        var failedTheme = await service.SetThemeAsync(AppThemeId.Light);
        var animation = await service.SetInterfaceAnimationsEnabledAsync(false);

        Assert.AreEqual(UiPreferenceUpdateStatus.Failed, failedTheme.Status);
        Assert.IsFalse(failedTheme.PersistenceStateUncertain);
        Assert.AreEqual(UiPreferenceUpdateStatus.Applied, animation.Status);
        Assert.AreEqual(AppLanguage.Hungarian, store.Current.Language);
        Assert.AreEqual(AppThemeId.Dark, store.Current.Theme);
        Assert.IsFalse(store.Current.InterfaceAnimationsEnabled);
        Assert.AreEqual(AppLanguage.Hungarian, service.CurrentLanguage);
        Assert.AreEqual(AppThemeId.Dark, service.CurrentThemeId);
        Assert.IsFalse(service.InterfaceAnimationsEnabled);
    }

    [TestMethod]
    public async Task ThemeReplacementPreservesEditorStateAndViewModelIdentity()
    {
        var store = new ScriptedPreferencesStore(DefaultPreferences());
        await using var service = await TestUiPreferencesFactory.CreateAsync(store: store);
        using var editor = new ThemeAwareDraftViewModel(service)
        {
            Draft = "unsaved secret draft",
            SearchText = "needle",
            Selection = 7,
            NavigationIndex = 2,
            IsDialogOpen = true,
            EnrollmentCode = "pending-enrollment"
        };
        var identity = editor;

        var result = await service.SetThemeAsync(AppThemeId.Light);

        Assert.AreEqual(UiPreferenceUpdateStatus.Applied, result.Status);
        Assert.AreSame(identity, editor);
        Assert.AreEqual(1, editor.ThemeChangeCount);
        Assert.AreEqual("unsaved secret draft", editor.Draft);
        Assert.AreEqual("needle", editor.SearchText);
        Assert.AreEqual(7, editor.Selection);
        Assert.AreEqual(2, editor.NavigationIndex);
        Assert.IsTrue(editor.IsDialogOpen);
        Assert.AreEqual("pending-enrollment", editor.EnrollmentCode);
    }

    private static ApplicationPreferences DefaultPreferences() =>
        new()
        {
            SchemaVersion = ApplicationPreferences.CurrentSchemaVersion,
            Language = AppLanguage.English,
            Theme = AppThemeId.Dark,
            InterfaceAnimationsEnabled = true
        };

    private sealed class ScriptedPreferencesStore(ApplicationPreferences initial) :
        IApplicationPreferencesStore
    {
        private readonly object _gate = new();
        private ApplicationPreferences _current = initial;
        private int _writeCount;
        private int _concurrentWrites;

        public Func<int, ApplicationPreferences, CancellationToken, Task>? BeforeWrite { get; init; }
        public HashSet<int> FailWriteNumbers { get; } = [];
        public List<ApplicationPreferences> SuccessfulWrites { get; } = [];
        public int MaximumConcurrentWrites { get; private set; }

        public ApplicationPreferences Current
        {
            get { lock (_gate) return _current; }
        }

        public Task<ApplicationPreferences> ReadAsync(CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(Current);
        }

        public async Task WriteAsync(
            ApplicationPreferences preferences,
            CancellationToken cancellationToken = default)
        {
            var concurrent = Interlocked.Increment(ref _concurrentWrites);
            MaximumConcurrentWrites = Math.Max(MaximumConcurrentWrites, concurrent);
            try
            {
                var writeNumber = Interlocked.Increment(ref _writeCount);
                if (BeforeWrite is not null)
                    await BeforeWrite(writeNumber, preferences, cancellationToken);
                cancellationToken.ThrowIfCancellationRequested();
                if (FailWriteNumbers.Contains(writeNumber))
                    throw new IOException("simulated preference write failure");
                lock (_gate)
                {
                    _current = preferences;
                    SuccessfulWrites.Add(preferences);
                }
            }
            finally
            {
                Interlocked.Decrement(ref _concurrentWrites);
            }
        }
    }

    private sealed class ThemeAwareDraftViewModel(UiPreferencesService preferences) :
        ViewModelBase(preferences)
    {
        public string Draft { get; set; } = string.Empty;
        public string SearchText { get; set; } = string.Empty;
        public int Selection { get; set; }
        public int NavigationIndex { get; set; }
        public bool IsDialogOpen { get; set; }
        public string EnrollmentCode { get; set; } = string.Empty;
        public int ThemeChangeCount { get; private set; }

        protected override void OnThemeChanged() => ThemeChangeCount++;
    }
}
