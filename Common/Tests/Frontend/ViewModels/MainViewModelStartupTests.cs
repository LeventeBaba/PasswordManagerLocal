using System.Reflection;
using System.Windows.Input;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using PasswordManagerLocal.Common.Contracts.Endpoints;
using PasswordManagerLocal.Common.Contracts.Preferences;
using PasswordManagerLocal.Common.Contracts.Responses;
using PasswordManagerLocal.Common.Contracts.Runtime;
using PasswordManagerLocal.Common.Frontend.Information;
using PasswordManagerLocal.Common.Frontend.Services;
using PasswordManagerLocal.Common.Frontend.ViewModels;
using PasswordManagerLocal.Common.Tests.Fakes;

namespace PasswordManagerLocal.Common.Tests.Frontend.ViewModels;

[TestClass]
public sealed class MainViewModelStartupTests
{
    [TestMethod]
    public async Task RememberedLogin_PresentsAuthenticatedPageWhileInitialListLoads()
    {
        await using var preferences = await TestUiPreferencesFactory.CreateAsync();
        var endpoints = CreateEndpoints(out var recording);
        var token = recording.AddProfile();
        recording.RestoreResult = Task.FromResult<IReadOnlyList<Guid>>(new[] { token });
        var list = new TaskCompletionSource<SavedPasswordsResponse>(TaskCreationOptions.RunContinuationsAsynchronously);
        recording.PasswordsResult = list.Task;
        await using var runtime = new StartupRuntime();
        using var shell = new MainViewModel(endpoints, runtime, new FakeBackgroundSyncSettingsClient(), preferences,
            deadlineScheduler: new ManualFrontendDeadlineScheduler());

        var initialization = shell.InitializeAsync();
        await recording.PasswordsRequested.Task;

        Assert.IsTrue(shell.IsAuthenticated);
        Assert.AreSame(shell.PasswordsViewModel, shell.CurrentPageViewModel);
        Assert.IsTrue(shell.PasswordsViewModel.IsInitialLoading);
        Assert.IsFalse(shell.PasswordsViewModel.IsVaultEmpty);
        Assert.IsFalse(shell.PasswordsViewModel.IsPasswordPaneContentEnabled);
        Assert.IsFalse(shell.PasswordsViewModel.CanAutomaticallyRefresh);
        list.SetResult(new SavedPasswordsResponse());
        await initialization;
        Assert.IsFalse(shell.PasswordsViewModel.IsInitialLoading);
        Assert.IsTrue(shell.PasswordsViewModel.IsVaultEmpty);
        Assert.IsTrue(shell.PasswordsViewModel.IsPasswordPaneContentEnabled);
    }

    [TestMethod]
    public async Task ConstructionPreparesLocalViewsWithoutStartingConnectionOrRestoration()
    {
        await using var preferences = await TestUiPreferencesFactory.CreateAsync();
        var endpoints = CreateEndpoints(out var recording);
        await using var runtime = new StartupRuntime();
        using var shell = new MainViewModel(endpoints, runtime, new FakeBackgroundSyncSettingsClient(), preferences);
        shell.LoginViewModel.Username = "test-user";
        shell.LoginViewModel.Password = "test-password";

        Assert.AreEqual(0, runtime.ConnectCalls);
        Assert.AreEqual(0, recording.RestoreCalls);
        Assert.AreSame(shell.LoginViewModel, shell.CurrentPageViewModel);
        Assert.IsFalse(shell.LoginViewModel.CanLogin);
    }

    [TestMethod]
    public async Task LoginPresentationReadinessAndDisposalDoNotConstructOptionalPagesOrReadCatalogs()
    {
        await using var preferences = await TestUiPreferencesFactory.CreateAsync();
        var endpoints = CreateEndpoints(out _);
        await using var runtime = new StartupRuntime();
        using var shell = new MainViewModel(endpoints, runtime, new FakeBackgroundSyncSettingsClient(), preferences);

        AssertOptionalPagesUncreated(shell);
        _ = shell.IsSettingsPageVisible;
        _ = shell.IsInformationPageVisible;
        _ = shell.IsSettingsSectionVisible;
        _ = shell.IsBackendPageEnabled;
        _ = shell.GetVisiblePages();
        _ = shell.CanRefreshPage(FrontendRefreshPage.BackgroundSync);
        AssertOptionalPagesUncreated(shell);

        await shell.InitializeAsync();
        AssertOptionalPagesUncreated(shell);
        Assert.IsNull(shell.AuthenticatedMenuContent);
        Assert.IsNull(shell.SessionRenewalDialogContent);
        Assert.IsNull(shell.DatabaseRecoveryDialogContent);

        shell.Dispose();
        AssertOptionalPagesUncreated(shell);
        Assert.Throws<ObjectDisposedException>(() => _ = shell.SettingsViewModel);
        Assert.Throws<ObjectDisposedException>(() => _ = shell.AboutViewModel);
        AssertOptionalPagesUncreated(shell);
    }

    [TestMethod]
    public async Task SettingsCreatedAfterReadinessUsesCurrentPreferencesAndIsReused()
    {
        await using var preferences = await TestUiPreferencesFactory.CreateAsync();
        var endpoints = CreateEndpoints(out _);
        var settingsClient = new FakeBackgroundSyncSettingsClient();
        await using var runtime = new StartupRuntime();
        using var shell = new MainViewModel(endpoints, runtime, settingsClient, preferences);
        await shell.InitializeAsync();
        await preferences.SetLanguageAsync(AppLanguage.Hungarian);
        await preferences.SetThemeAsync(AppThemeId.Light);
        AssertOptionalPagesUncreated(shell);

        ((ICommand)shell.ShowSettingsCommand).Execute(null);
        var settings = shell.SettingsViewModel;
        Assert.AreSame(settings, shell.CurrentPageViewModel);
        Assert.AreEqual(AppLanguage.Hungarian, settings.CurrentLanguage);
        Assert.AreEqual(AppThemeId.Light, settings.CurrentThemeId);
        Assert.IsTrue(settings.CanAutomaticallyRefreshBackgroundSync);
        Assert.AreEqual(1, settingsClient.GetCount);
        AssertPublicCatalogsUncreated(shell);

        Assert.IsTrue(await shell.TryNavigateBackAsync());
        ((ICommand)shell.ShowSettingsCommand).Execute(null);
        Assert.AreSame(settings, shell.SettingsViewModel);
        shell.Dispose();
        Assert.IsTrue(settings.IsDisposed);
    }

    [TestMethod]
    public async Task InformationCatalogsLoadOnlyWhenTheirPagesAreRequested()
    {
        await using var preferences = await TestUiPreferencesFactory.CreateAsync();
        var endpoints = CreateEndpoints(out _);
        await using var runtime = new StartupRuntime();
        using var shell = new MainViewModel(endpoints, runtime, new FakeBackgroundSyncSettingsClient(), preferences);
        ((ICommand)shell.ShowSettingsCommand).Execute(null);
        AssertPublicCatalogsUncreated(shell);

        ((ICommand)shell.SettingsViewModel.NavigateToAboutCommand).Execute(null);
        var about = shell.AboutViewModel;
        Assert.AreSame(about, shell.CurrentPageViewModel);
        Assert.IsTrue(ReadShellField<Lazy<ThirdPartyNoticeCatalog>>(shell, "_noticeCatalog").IsValueCreated);
        Assert.IsFalse(ReadShellField<Lazy<HelpCatalog>>(shell, "_helpCatalog").IsValueCreated);
        Assert.IsNull(ReadShellField<object?>(shell, "_thirdPartyLicensesViewModel"));
        Assert.IsNull(ReadShellField<object?>(shell, "_privacyPolicyViewModel"));

        ((ICommand)about.NavigateToLicensesCommand).Execute(null);
        var licenses = shell.ThirdPartyLicensesViewModel;
        Assert.AreSame(licenses, shell.CurrentPageViewModel);
        Assert.IsTrue(await shell.TryNavigateBackAsync());
        Assert.AreSame(about, shell.CurrentPageViewModel);
        Assert.IsTrue(await shell.TryNavigateBackAsync());
        Assert.AreSame(shell.SettingsViewModel, shell.CurrentPageViewModel);
        ((ICommand)shell.SettingsViewModel.NavigateToHelpCommand).Execute(null);
        Assert.IsTrue(ReadShellField<Lazy<HelpCatalog>>(shell, "_helpCatalog").IsValueCreated);
        Assert.IsNull(ReadShellField<object?>(shell, "_privacyPolicyViewModel"));
        Assert.IsTrue(await shell.TryNavigateBackAsync());
        ((ICommand)shell.SettingsViewModel.NavigateToPrivacyCommand).Execute(null);
        var privacy = shell.PrivacyPolicyViewModel;
        Assert.AreSame(privacy, shell.CurrentPageViewModel);
        Assert.IsTrue(await shell.TryNavigateBackAsync());
        Assert.AreSame(privacy, shell.PrivacyPolicyViewModel);

        shell.Dispose();
        Assert.IsTrue(about.IsDisposed);
        Assert.IsTrue(licenses.IsDisposed);
        Assert.IsTrue(privacy.IsDisposed);
    }

    private static void AssertOptionalPagesUncreated(MainViewModel shell)
    {
        foreach (var name in new[] { "_settingsViewModel", "_aboutViewModel", "_privacyPolicyViewModel",
                     "_thirdPartyLicensesViewModel", "_thirdPartyLicenseDetailViewModel" })
            Assert.IsNull(ReadShellField<object?>(shell, name), $"{name} must wait for explicit use.");
        AssertPublicCatalogsUncreated(shell);
    }

    private static void AssertPublicCatalogsUncreated(MainViewModel shell)
    {
        Assert.IsFalse(ReadShellField<Lazy<HelpCatalog>>(shell, "_helpCatalog").IsValueCreated);
        Assert.IsFalse(ReadShellField<Lazy<ThirdPartyNoticeCatalog>>(shell, "_noticeCatalog").IsValueCreated);
    }

    private static T ReadShellField<T>(MainViewModel shell, string name) =>
        (T)typeof(MainViewModel).GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(shell)!;

    [TestMethod]
    public async Task AuthenticationStaysDisabledUntilRememberedSessionDecisionCompletes()
    {
        await using var preferences = await TestUiPreferencesFactory.CreateAsync();
        var endpoints = CreateEndpoints(out var recording);
        var release = new TaskCompletionSource<IReadOnlyList<Guid>>(TaskCreationOptions.RunContinuationsAsynchronously);
        recording.RestoreResult = release.Task;
        await using var runtime = new StartupRuntime();
        using var shell = new MainViewModel(endpoints, runtime, new FakeBackgroundSyncSettingsClient(), preferences);
        shell.LoginViewModel.Username = "typed-user";
        shell.LoginViewModel.Password = "typed-password";

        var initialization = shell.InitializeAsync();
        Assert.IsTrue(shell.IsStartupInProgress);
        Assert.IsFalse(shell.LoginViewModel.CanLogin);
        Assert.IsFalse(shell.RegistrationViewModel.IsBackendInitialized);
        Assert.IsFalse(shell.CanRetryStartup);
        release.SetResult(Array.Empty<Guid>());
        await initialization;

        Assert.IsFalse(shell.IsStartupInProgress);
        Assert.IsTrue(shell.LoginViewModel.CanLogin);
        Assert.IsTrue(shell.RegistrationViewModel.IsBackendInitialized);
        Assert.AreEqual("typed-user", shell.LoginViewModel.Username);
        Assert.IsFalse(shell.CanRetryStartup);
    }

    [TestMethod]
    public async Task ConcurrentAndRepeatedInitializationRestoresOnlyOnce()
    {
        await using var preferences = await TestUiPreferencesFactory.CreateAsync();
        var endpoints = CreateEndpoints(out var recording);
        var release = new TaskCompletionSource<IReadOnlyList<Guid>>(TaskCreationOptions.RunContinuationsAsynchronously);
        recording.RestoreResult = release.Task;
        await using var runtime = new StartupRuntime();
        using var shell = new MainViewModel(endpoints, runtime, new FakeBackgroundSyncSettingsClient(), preferences);

        var first = shell.InitializeAsync();
        var second = shell.InitializeAsync();
        Assert.AreSame(first, second);
        Assert.AreEqual(1, recording.RestoreCalls);
        release.SetResult(Array.Empty<Guid>());
        await Task.WhenAll(first, second);
        await shell.InitializeAsync();

        Assert.AreEqual(1, recording.RestoreCalls);
        Assert.AreEqual(1, runtime.ConnectCalls);
    }

    [TestMethod]
    public async Task CompletedReadinessWaitStillRequiresCurrentReadySnapshot()
    {
        await using var preferences = await TestUiPreferencesFactory.CreateAsync();
        var endpoints = CreateEndpoints(out var recording);
        await using var runtime = new StartupRuntime
        {
            MarkReadyOnConnect = false,
            CompleteReadinessWithoutReadySnapshot = true
        };
        runtime.SetState(BackendRuntimeState.Starting, publish: false);
        using var shell = new MainViewModel(endpoints, runtime, new FakeBackgroundSyncSettingsClient(), preferences);
        shell.LoginViewModel.Username = "typed-user";
        shell.LoginViewModel.Password = "typed-password";

        await shell.InitializeAsync();

        Assert.AreEqual(1, runtime.ConnectCalls);
        Assert.AreEqual(1, runtime.ReadinessWaitCalls);
        Assert.AreEqual(0, recording.RestoreCalls, "A completed wait cannot authorize restoration against a non-ready snapshot.");
        Assert.IsFalse(shell.LoginViewModel.CanLogin);
        Assert.IsFalse(shell.RegistrationViewModel.IsBackendInitialized);
        Assert.IsFalse(shell.IsAuthenticated);
        Assert.IsFalse(shell.IsStartupInProgress);
        Assert.IsTrue(shell.CanRetryStartup);
    }

    [TestMethod]
    public async Task HostConnectionIsJoinedAndFailedAttemptCanBeRetried()
    {
        await using var preferences = await TestUiPreferencesFactory.CreateAsync();
        var endpoints = CreateEndpoints(out var recording);
        await using var runtime = new StartupRuntime();
        runtime.SetState(BackendRuntimeState.Failed, publish: false);
        var startup = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var shell = new MainViewModel(endpoints, runtime, new FakeBackgroundSyncSettingsClient(), preferences,
            backendConnectionStartup: startup.Task);

        var initialization = shell.InitializeAsync();
        Assert.IsFalse(initialization.IsCompleted);
        Assert.AreEqual(0, runtime.ConnectCalls);
        startup.SetException(new IOException("Simulated startup failure"));
        await initialization;
        Assert.IsTrue(shell.CanRetryStartup);
        Assert.IsFalse(shell.LoginViewModel.IsBackendInitialized);

        await shell.InitializeAsync();
        Assert.AreEqual(1, runtime.ConnectCalls);
        Assert.AreEqual(1, recording.RestoreCalls);
        Assert.IsTrue(shell.LoginViewModel.IsBackendInitialized);
        Assert.IsFalse(shell.CanRetryStartup);
    }

    [TestMethod]
    public async Task LocalSettingsRemainAvailableWhileConnectionIsPending()
    {
        await using var preferences = await TestUiPreferencesFactory.CreateAsync();
        var endpoints = CreateEndpoints(out var recording);
        var settingsClient = new FakeBackgroundSyncSettingsClient();
        await using var runtime = new StartupRuntime();
        runtime.SetState(BackendRuntimeState.Starting, publish: false);
        var startup = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var shell = new MainViewModel(endpoints, runtime, settingsClient, preferences,
            backendConnectionStartup: startup.Task);

        var initialization = shell.InitializeAsync();
        ((ICommand)shell.ShowSettingsCommand).Execute(null);
        Assert.IsTrue(shell.IsSettingsPageVisible);
        Assert.IsTrue(shell.IsApplicationSurfaceEnabled);
        Assert.IsTrue(shell.IsBackendPageEnabled);
        Assert.IsFalse(shell.SettingsViewModel.IsBackgroundSyncToggleEnabled);
        Assert.AreEqual(0, settingsClient.GetCount);
        runtime.SetState(BackendRuntimeState.Ready, publish: false);
        startup.SetResult();
        await initialization;
        Assert.AreEqual(1, recording.RestoreCalls);
    }

    [TestMethod]
    public async Task AutomaticSignInDoesNotReplaceSettingsTheUserOpenedDuringStartup()
    {
        await using var preferences = await TestUiPreferencesFactory.CreateAsync();
        var endpoints = CreateEndpoints(out var recording);
        var token = recording.AddProfile();
        var release = new TaskCompletionSource<IReadOnlyList<Guid>>(TaskCreationOptions.RunContinuationsAsynchronously);
        recording.RestoreResult = release.Task;
        await using var runtime = new StartupRuntime();
        using var shell = new MainViewModel(endpoints, runtime, new FakeBackgroundSyncSettingsClient(), preferences,
            deadlineScheduler: new ManualFrontendDeadlineScheduler());

        var initialization = shell.InitializeAsync();
        ((ICommand)shell.ShowSettingsCommand).Execute(null);
        release.SetResult(new[] { token });
        await initialization;

        Assert.IsTrue(shell.IsAuthenticated);
        Assert.IsTrue(shell.IsSettingsPageVisible);
        await shell.InitializeAsync();
        Assert.AreEqual(1, recording.RestoreCalls);
    }

    [TestMethod]
    public async Task MultipleRememberedProfilesAreOfferedWithoutAutomaticallySelectingTheFirst()
    {
        await using var preferences = await TestUiPreferencesFactory.CreateAsync();
        var endpoints = CreateEndpoints(out var recording);
        var first = recording.AddProfile();
        var second = recording.AddProfile();
        recording.RestoreResult = Task.FromResult<IReadOnlyList<Guid>>(new[] { first, second });
        await using var runtime = new StartupRuntime();
        using var shell = new MainViewModel(endpoints, runtime, new FakeBackgroundSyncSettingsClient(), preferences,
            deadlineScheduler: new ManualFrontendDeadlineScheduler());

        await shell.InitializeAsync();

        Assert.IsFalse(shell.IsAuthenticated);
        Assert.AreSame(shell.ChangeProfileViewModel, shell.CurrentPageViewModel);
        Assert.IsTrue(shell.ChangeProfileViewModel.IsStartupSelection);
        Assert.AreEqual(2, shell.ChangeProfileViewModel.Profiles.Count);
        await shell.InitializeAsync();
        Assert.AreEqual(1, recording.RestoreCalls);
    }

    [TestMethod]
    public async Task RetiredConnectionCannotPublishItsRememberedSessions()
    {
        await using var preferences = await TestUiPreferencesFactory.CreateAsync();
        var endpoints = CreateEndpoints(out var recording);
        var token = recording.AddProfile();
        var release = new TaskCompletionSource<IReadOnlyList<Guid>>(TaskCreationOptions.RunContinuationsAsynchronously);
        recording.RestoreResult = release.Task;
        await using var runtime = new StartupRuntime();
        var registry = new AuthSessionRegistry();
        using var shell = new MainViewModel(endpoints, runtime, new FakeBackgroundSyncSettingsClient(), preferences,
            registry, deadlineScheduler: new ManualFrontendDeadlineScheduler());

        var initialization = shell.InitializeAsync();
        runtime.SetState(BackendRuntimeState.Failed, publish: true);
        release.SetResult(new[] { token });
        await initialization;

        Assert.IsFalse(shell.IsAuthenticated);
        Assert.AreEqual(0, registry.ListTokens().Count);
        Assert.IsFalse(shell.LoginViewModel.IsBackendInitialized);
        Assert.IsTrue(shell.CanRetryStartup);
        registry.Dispose();
    }

    [TestMethod]
    public async Task TemporarilyUnavailableSavedKeyCanBeRetriedWithoutReopeningTheUi()
    {
        await using var preferences = await TestUiPreferencesFactory.CreateAsync();
        var endpoints = CreateEndpoints(out var recording);
        recording.RestoreResult = Task.FromException<IReadOnlyList<Guid>>(
            new KeyProtectorUnavailableException(KeyProtectorUnavailableReason.DeviceLocked));
        await using var runtime = new StartupRuntime();
        using var shell = new MainViewModel(endpoints, runtime, new FakeBackgroundSyncSettingsClient(), preferences);

        await shell.InitializeAsync();
        Assert.IsTrue(shell.CanRetryStartup);
        Assert.IsTrue(shell.LoginViewModel.IsBackendInitialized);
        Assert.IsFalse(shell.IsDatabaseRecoveryDialogOpen);
        recording.RestoreResult = Task.FromResult<IReadOnlyList<Guid>>(Array.Empty<Guid>());

        await shell.InitializeAsync();

        Assert.AreEqual(2, recording.RestoreCalls);
        Assert.IsFalse(shell.CanRetryStartup);
        Assert.IsTrue(shell.LoginViewModel.IsBackendInitialized);
    }

    [TestMethod]
    public async Task RestoreFailureAllowsManualLoginWithoutAnAutomaticRetryLoop()
    {
        await using var preferences = await TestUiPreferencesFactory.CreateAsync();
        var endpoints = CreateEndpoints(out var recording);
        recording.RestoreResult = Task.FromException<IReadOnlyList<Guid>>(new IOException("Simulated restore failure"));
        await using var runtime = new StartupRuntime();
        using var shell = new MainViewModel(endpoints, runtime, new FakeBackgroundSyncSettingsClient(), preferences);

        await shell.InitializeAsync();
        await shell.InitializeAsync();

        Assert.IsTrue(shell.LoginViewModel.IsBackendInitialized);
        Assert.IsFalse(shell.IsStartupInProgress);
        Assert.AreEqual(1, recording.RestoreCalls);
    }

    private static IEndpoints CreateEndpoints(out StartupEndpoints recording)
    {
        var endpoints = DispatchProxy.Create<IEndpoints, StartupEndpoints>();
        recording = (StartupEndpoints)(object)endpoints;
        return endpoints;
    }

    public class StartupEndpoints : DispatchProxy
    {
        public int RestoreCalls { get; private set; }
        public Task<IReadOnlyList<Guid>> RestoreResult { get; set; } =
            Task.FromResult<IReadOnlyList<Guid>>(Array.Empty<Guid>());
        public Task<SavedPasswordsResponse>? PasswordsResult { get; set; }
        public TaskCompletionSource PasswordsRequested { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly Dictionary<Guid, UserProfileInfoResponse> _profiles = new();

        public Guid AddProfile()
        {
            var token = Guid.NewGuid();
            _profiles.Add(token, new UserProfileInfoResponse
            {
                UId = Guid.NewGuid(), Username = "test-user", FirstName = "Test", IsRememberMeEnabled = true
            });
            return token;
        }

        protected override object? Invoke(MethodInfo? method, object?[]? args)
        {
            switch (method?.Name)
            {
                case nameof(IEndpoints.RestoreRememberedSessionsAsync):
                    RestoreCalls++;
                    return RestoreResult;
                case nameof(IEndpoints.GetAuthSessionStatusAsync):
                    return Task.FromResult(new AuthSessionStatusResponse { IsAuthenticated = true });
                case nameof(IEndpoints.GetUserProfileInfoAsync):
                    return Task.FromResult(_profiles[(Guid)args![0]!]);
                case nameof(IEndpoints.GetSavedPasswordsAsync):
                    PasswordsRequested.TrySetResult();
                    return PasswordsResult ?? Task.FromResult(new SavedPasswordsResponse());
                case nameof(IEndpoints.LogoutAsync):
                    return Task.CompletedTask;
                default:
                    throw new InvalidOperationException($"Unexpected endpoint: {method?.Name}");
            }
        }
    }

    private sealed class StartupRuntime : IBackendRuntimeClient
    {
        public int ConnectCalls { get; private set; }
        public int ReadinessWaitCalls { get; private set; }
        public bool MarkReadyOnConnect { get; init; } = true;
        public bool CompleteReadinessWithoutReadySnapshot { get; init; }
        public BackendRuntimeSnapshot Snapshot { get; private set; } =
            new(BackendRuntimeState.Ready, BackendRuntimeFailureKind.None, null, DateTimeOffset.UtcNow);
        public event EventHandler<BackendRuntimeStateChangedEventArgs>? StateChanged;
        public Task ConnectAsync(CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            ConnectCalls++;
            if (MarkReadyOnConnect)
                SetState(BackendRuntimeState.Ready, publish: false);
            return Task.CompletedTask;
        }
        public Task WaitUntilReadyAsync(CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            ReadinessWaitCalls++;
            return Snapshot.IsReady || CompleteReadinessWithoutReadySnapshot
                ? Task.CompletedTask : Task.FromException(new IOException());
        }
        public void SetState(BackendRuntimeState state, bool publish)
        {
            var previous = Snapshot;
            Snapshot = new BackendRuntimeSnapshot(state, BackendRuntimeFailureKind.None, null, DateTimeOffset.UtcNow);
            if (publish)
                StateChanged?.Invoke(this, new BackendRuntimeStateChangedEventArgs(previous, Snapshot));
        }
        public Task ResetDatabaseAndRestartAsync(CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
