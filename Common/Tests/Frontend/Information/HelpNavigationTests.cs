using System.Reflection;
using System.Windows.Input;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using PasswordManagerLocal.Common.Contracts.Endpoints;
using PasswordManagerLocal.Common.Contracts.Preferences;
using PasswordManagerLocal.Common.Contracts.Runtime;
using PasswordManagerLocal.Common.Frontend.Information;
using PasswordManagerLocal.Common.Frontend.Localization;
using PasswordManagerLocal.Common.Frontend.ViewModels;
using PasswordManagerLocal.Common.Frontend.ViewModels.Information;
using PasswordManagerLocal.Common.Tests.Fakes;

namespace PasswordManagerLocal.Common.Tests.Frontend.Information;

[TestClass]
public sealed class HelpNavigationTests
{
    [TestMethod]
    public async Task DirectTopicAndFallbackWorkWithoutBackendAndPreserveCallerFields()
    {
        await using var preferences = await TestUiPreferencesFactory.CreateAsync();
        var endpoints = DispatchProxy.Create<IEndpoints, RejectAllEndpoints>();
        await using var runtime = new UnavailableRuntime();
        using var shell = new MainViewModel(endpoints, runtime, new FakeBackgroundSyncSettingsClient(), preferences);
        var caller = shell.LoginViewModel;
        caller.Username = "unfinished-local-name";
        caller.Password = "unfinished-form-value";

        ExecuteCommand(caller.ShowRememberMeSecurityAdviceCommand);
        var loginHelp = (HelpPageViewModel)shell.CurrentPageViewModel;
        Assert.AreEqual(HelpTopicIds.RememberMe, loginHelp.Route.ContentId);
        Assert.IsTrue(shell.IsInformationPageVisible);
        Assert.IsTrue(await shell.TryNavigateBackAsync());
        Assert.AreSame(caller, shell.CurrentPageViewModel);
        Assert.AreEqual("unfinished-local-name", caller.Username);
        Assert.AreEqual("unfinished-form-value", caller.Password);

        ExecuteCommand(shell.ShowRegistrationCommand);
        var registration = shell.RegistrationViewModel;
        registration.Username = "unfinished-registration";
        registration.Password = "unfinished-registration-password";
        registration.RememberMe = true;
        ExecuteCommand(registration.ShowRememberMeSecurityAdviceCommand);
        var registrationHelp = (HelpPageViewModel)shell.CurrentPageViewModel;
        Assert.AreEqual(HelpTopicIds.RememberMe, registrationHelp.Route.ContentId);
        Assert.IsTrue(await shell.TryNavigateBackAsync());
        Assert.AreSame(registration, shell.CurrentPageViewModel);
        Assert.AreEqual("unfinished-registration", registration.Username);
        Assert.AreEqual("unfinished-registration-password", registration.Password);
        Assert.IsTrue(registration.RememberMe);

        shell.OpenHelpTopic("unknown/topic");
        var fallback = (HelpPageViewModel)shell.CurrentPageViewModel;
        Assert.AreEqual(InformationDestination.Help, fallback.Route.Destination);
        Assert.IsTrue(fallback.IsTopicUnavailable);
        Assert.AreEqual(4, fallback.Cards.Count);
        Assert.IsTrue(await shell.TryNavigateBackAsync());
        Assert.AreSame(registration, shell.CurrentPageViewModel);
        Assert.AreEqual(0, ((RejectAllEndpoints)(object)endpoints).Calls);
    }

    [TestMethod]
    public async Task SettingsHelpReturnsToTheSameUnfinishedRegistration()
    {
        await using var preferences = await TestUiPreferencesFactory.CreateAsync();
        var endpoints = DispatchProxy.Create<IEndpoints, RejectAllEndpoints>();
        await using var runtime = new UnavailableRuntime();
        using var shell = new MainViewModel(endpoints, runtime, new FakeBackgroundSyncSettingsClient(), preferences);
        ExecuteCommand(shell.ShowRegistrationCommand);
        var caller = shell.RegistrationViewModel;
        caller.Username = "unfinished-registration";
        caller.Password = "unfinished-form-value";
        ExecuteCommand(shell.ShowSettingsCommand);
        shell.OpenHelpTopic(HelpTopicIds.CreateAccount);

        Assert.IsTrue(await shell.TryNavigateBackAsync());
        Assert.AreSame(shell.SettingsViewModel, shell.CurrentPageViewModel);
        Assert.IsTrue(await shell.TryNavigateBackAsync());
        Assert.AreSame(caller, shell.CurrentPageViewModel);
        Assert.AreEqual("unfinished-registration", caller.Username);
        Assert.AreEqual("unfinished-form-value", caller.Password);
        Assert.AreEqual(0, ((RejectAllEndpoints)(object)endpoints).Calls);
    }

    [TestMethod]
    public async Task ReturnToAppSkipsDeepHelpHistoryAndPreservesTheOriginalForm()
    {
        await using var preferences = await TestUiPreferencesFactory.CreateAsync();
        var endpoints = DispatchProxy.Create<IEndpoints, RejectAllEndpoints>();
        await using var runtime = new UnavailableRuntime();
        using var shell = new MainViewModel(endpoints, runtime, new FakeBackgroundSyncSettingsClient(), preferences);
        ExecuteCommand(shell.ShowRegistrationCommand);
        var caller = shell.RegistrationViewModel;
        caller.Username = "unfinished-registration";
        caller.Password = "unfinished-form-value";
        ExecuteCommand(shell.ShowSettingsCommand);
        ExecuteCommand(shell.SettingsViewModel.NavigateToHelpCommand);
        for (var step = 0; step < 40; step++)
            shell.OpenHelpTopic(step % 2 == 0 ? HelpTopicIds.Sync : HelpTopicIds.RememberMe);

        ExecuteCommand(((HelpPageViewModel)shell.CurrentPageViewModel).ReturnToAppCommand);

        Assert.AreSame(caller, shell.CurrentPageViewModel);
        Assert.AreEqual("unfinished-registration", caller.Username);
        Assert.AreEqual("unfinished-form-value", caller.Password);
        shell.OpenHelpTopic(HelpTopicIds.CreateAccount);
        Assert.IsTrue(await shell.TryNavigateBackAsync());
        Assert.AreSame(caller, shell.CurrentPageViewModel, "A new visit must not inherit the previous Back stack.");
        Assert.AreEqual(0, ((RejectAllEndpoints)(object)endpoints).Calls);
    }

    [TestMethod]
    public async Task SearchResultsAndReadingPositionSurviveOpeningATopicAndGoingBack()
    {
        await using var preferences = await TestUiPreferencesFactory.CreateAsync();
        var endpoints = DispatchProxy.Create<IEndpoints, RejectAllEndpoints>();
        await using var runtime = new UnavailableRuntime();
        using var shell = new MainViewModel(endpoints, runtime, new FakeBackgroundSyncSettingsClient(), preferences);
        shell.OpenHelpTopic(HelpTopicIds.RememberMe);
        ExecuteCommand(((HelpPageViewModel)shell.CurrentPageViewModel).FindHelpCommand);
        var hub = (HelpPageViewModel)shell.CurrentPageViewModel;
        Assert.IsTrue(hub.IsHelpHome);
        hub.SearchText = "clipboard";
        Assert.IsTrue(hub.IsSearchActive);
        Assert.IsTrue(hub.Cards.Count > 0);
        Assert.IsTrue(hub.Cards.All(card => card.HasCategoryLabel));
        var result = hub.Cards.First();
        hub.ScrollOffset = 160;
        hub.FocusedCardId = result.Id;

        ExecuteCommand(result.OpenCommand);
        var article = (HelpPageViewModel)shell.CurrentPageViewModel;
        Assert.AreEqual(result.Id, article.Route.ContentId);
        Assert.IsTrue(article.HasSections);
        Assert.IsTrue(await shell.TryNavigateBackAsync());

        Assert.AreSame(hub, shell.CurrentPageViewModel);
        Assert.AreEqual("clipboard", hub.SearchText);
        Assert.AreEqual(160d, hub.ScrollOffset);
        Assert.AreEqual(result.Id, hub.FocusedCardId);
        hub.SearchText = "no-such-guidance-zzzz";
        Assert.IsTrue(hub.HasNoSearchResults);
        ExecuteCommand(hub.ClearSearchCommand);
        Assert.IsFalse(hub.IsSearchActive);
        Assert.AreEqual(4, hub.Cards.Count);
        Assert.AreEqual(0d, hub.ScrollOffset);
        Assert.AreEqual(0, ((RejectAllEndpoints)(object)endpoints).Calls);
    }

    [TestMethod]
    public async Task CategoryShortcutAndQuickReturnRemainValidAfterLogout()
    {
        await using var preferences = await TestUiPreferencesFactory.CreateAsync();
        var endpoints = DispatchProxy.Create<IEndpoints, RejectAllEndpoints>();
        await using var runtime = new UnavailableRuntime();
        using var shell = new MainViewModel(endpoints, runtime, new FakeBackgroundSyncSettingsClient(), preferences);
        shell.OpenHelpTopic(HelpTopicIds.RememberMe);
        ExecuteCommand(((HelpPageViewModel)shell.CurrentPageViewModel).NavigateToCategoryCommand);
        var category = (HelpPageViewModel)shell.CurrentPageViewModel;
        Assert.AreEqual(InformationDestination.HelpCategory, category.Route.Destination);
        Assert.AreEqual("security-tips", category.Route.ContentId);
        ExecuteCommand(category.Cards.First().OpenCommand);
        var article = (HelpPageViewModel)shell.CurrentPageViewModel;

        await shell.RequestLogoutAsync();
        Assert.AreSame(article, shell.CurrentPageViewModel);
        ExecuteCommand(article.ReturnToAppCommand);

        Assert.AreSame(shell.LoginViewModel, shell.CurrentPageViewModel);
        Assert.IsFalse(shell.IsAuthenticated);
        Assert.AreEqual(0, ((RejectAllEndpoints)(object)endpoints).Calls);
    }

    [TestMethod]
    public async Task ChangingLanguageRecomputesSearchWithoutLosingTheQuery()
    {
        await using var preferences = await TestUiPreferencesFactory.CreateAsync(
            resourceLoader: new ShippedLocalizationLoader());
        var catalog = HelpCatalogLoader.Load();
        using var hub = new HelpPageViewModel(preferences, catalog,
            new InformationRoute(InformationDestination.Help), () => { }, _ => { }, () => { });
        hub.SearchText = "offline";
        var englishTitle = hub.Title;

        var changed = await preferences.SetLanguageAsync(AppLanguage.Hungarian);

        Assert.IsTrue(changed.IsSuccess);
        Assert.AreEqual("offline", hub.SearchText);
        Assert.AreNotEqual(englishTitle, hub.Title);
        var expected = catalog.Search(AppLanguage.Hungarian, HelpPlatform.Windows, hub.SearchText);
        CollectionAssert.AreEqual(expected.Select(result => result.Topic.Id).ToArray(),
            hub.Cards.Select(card => card.Id).ToArray());
    }

    [TestMethod]
    public async Task SearchResultStatusExplainsEmptyResultsAndRefreshesWithLanguage()
    {
        await using var preferences = await TestUiPreferencesFactory.CreateAsync(
            resourceLoader: new ShippedLocalizationLoader());
        using var page = new HelpPageViewModel(preferences, HelpCatalogLoader.Load(),
            new InformationRoute(InformationDestination.Help), () => { }, _ => { }, () => { });
        page.SearchText = "no-such-help-topic-zzzz";
        Assert.IsTrue(page.HasNoSearchResults);
        StringAssert.Contains(page.SearchResultsStatusText, page.NoSearchResultsText);
        var englishHint = page.SearchKeyboardHelpText;
        var englishStatus = page.SearchResultsStatusText;
        Assert.IsTrue((await preferences.SetLanguageAsync(AppLanguage.Hungarian)).IsSuccess);
        Assert.AreNotEqual(englishHint, page.SearchKeyboardHelpText);
        Assert.AreNotEqual(englishStatus, page.SearchResultsStatusText);
        StringAssert.Contains(page.SearchResultsStatusText, page.NoSearchResultsText);
        ExecuteCommand(page.ClearSearchCommand);
        Assert.IsFalse(page.IsSearchActive);
        Assert.AreEqual(4, page.Cards.Count);
        Assert.AreEqual(page.CardsLabel, page.SearchResultsStatusText);
    }

    [TestMethod]
    public async Task SettingsHelpRelatedTopicsAndAboutKeepTheirBackPaths()
    {
        await using var preferences = await TestUiPreferencesFactory.CreateAsync();
        var endpoints = DispatchProxy.Create<IEndpoints, RejectAllEndpoints>();
        await using var runtime = new UnavailableRuntime();
        using var shell = new MainViewModel(endpoints, runtime, new FakeBackgroundSyncSettingsClient(), preferences);
        ExecuteCommand(shell.ShowSettingsCommand);
        ExecuteCommand(shell.SettingsViewModel.NavigateToHelpCommand);
        var hub = (HelpPageViewModel)shell.CurrentPageViewModel;
        ExecuteCommand(hub.Cards.Single(card => card.Id == "user-guide").OpenCommand);
        var category = (HelpPageViewModel)shell.CurrentPageViewModel;
        ExecuteCommand(category.Cards.Single(card => card.Id == HelpTopicIds.Disconnect).OpenCommand);
        var firstTopic = (HelpPageViewModel)shell.CurrentPageViewModel;
        firstTopic.ScrollOffset = 123;
        shell.OpenHelpTopic(HelpTopicIds.Sync);

        Assert.IsTrue(await shell.TryNavigateBackAsync());
        Assert.AreSame(firstTopic, shell.CurrentPageViewModel);
        Assert.AreEqual(123d, firstTopic.ScrollOffset);
        Assert.IsTrue(await shell.TryNavigateBackAsync());
        Assert.AreSame(category, shell.CurrentPageViewModel);
        Assert.IsTrue(await shell.TryNavigateBackAsync());
        Assert.AreSame(hub, shell.CurrentPageViewModel);
        Assert.IsTrue(await shell.TryNavigateBackAsync());
        Assert.AreSame(shell.SettingsViewModel, shell.CurrentPageViewModel);

        ExecuteCommand(shell.SettingsViewModel.NavigateToAboutCommand);
        ExecuteCommand(shell.AboutViewModel.NavigateToLicensesCommand);
        Assert.AreSame(shell.ThirdPartyLicensesViewModel, shell.CurrentPageViewModel);
        Assert.IsTrue(await shell.TryNavigateBackAsync());
        Assert.AreSame(shell.AboutViewModel, shell.CurrentPageViewModel);
        Assert.IsTrue(await shell.TryNavigateBackAsync());
        Assert.AreSame(shell.SettingsViewModel, shell.CurrentPageViewModel);
        Assert.IsTrue(await shell.TryNavigateBackAsync());
        Assert.AreSame(shell.LoginViewModel, shell.CurrentPageViewModel);
        Assert.AreEqual(0, ((RejectAllEndpoints)(object)endpoints).Calls);
    }

    [TestMethod]
    public async Task LogoutCleanupKeepsPublicDocumentationOpen()
    {
        await using var preferences = await TestUiPreferencesFactory.CreateAsync();
        var endpoints = DispatchProxy.Create<IEndpoints, RejectAllEndpoints>();
        await using var runtime = new UnavailableRuntime();
        using var shell = new MainViewModel(endpoints, runtime, new FakeBackgroundSyncSettingsClient(), preferences);
        shell.OpenHelpTopic(HelpTopicIds.MasterPassword);
        var topic = shell.CurrentPageViewModel;

        await shell.RequestLogoutAsync();

        Assert.AreSame(topic, shell.CurrentPageViewModel);
        Assert.IsFalse(shell.IsAuthenticated);
        Assert.IsTrue(await shell.TryNavigateBackAsync());
        Assert.AreSame(shell.LoginViewModel, shell.CurrentPageViewModel);
        Assert.AreEqual(0, ((RejectAllEndpoints)(object)endpoints).Calls);
    }

    [TestMethod]
    public async Task LanguageChangeKeepsTheTopicAndReadingState()
    {
        await using var preferences = await TestUiPreferencesFactory.CreateAsync(
            resourceLoader: new ShippedLocalizationLoader());
        using var page = new HelpPageViewModel(preferences, HelpCatalogLoader.Load(),
            new InformationRoute(InformationDestination.HelpTopic, HelpTopicIds.AddDevice), () => { }, _ => { }, () => { });
        var englishTitle = page.Title;
        var sectionCount = page.Sections.Count;
        page.ScrollOffset = 240;
        page.FocusedCardId = HelpTopicIds.EnrollmentOverview;

        var result = await preferences.SetLanguageAsync(AppLanguage.Hungarian);

        Assert.IsTrue(result.IsSuccess);
        Assert.AreNotEqual(englishTitle, page.Title);
        Assert.AreEqual("Eszköz hozzáadása a bejelentkezett profilból", page.Title);
        Assert.AreEqual(HelpTopicIds.AddDevice, page.Route.ContentId);
        Assert.AreEqual(sectionCount, page.Sections.Count);
        Assert.AreEqual(240d, page.ScrollOffset);
        Assert.AreEqual(HelpTopicIds.EnrollmentOverview, page.FocusedCardId);
        Assert.IsTrue(page.Sections.Any(section => section.Label == "Windows"));
        Assert.IsFalse(page.Sections.Any(section => section.Label == "Android"));
    }

    [TestMethod]
    public async Task SecurityAndTroubleshootingAreReadableWithoutBackendAndReturnToUnfinishedLogin()
    {
        await using var preferences = await TestUiPreferencesFactory.CreateAsync();
        var endpoints = DispatchProxy.Create<IEndpoints, RejectAllEndpoints>();
        await using var runtime = new UnavailableRuntime();
        using var shell = new MainViewModel(endpoints, runtime, new FakeBackgroundSyncSettingsClient(), preferences);
        var caller = shell.LoginViewModel;
        caller.Username = "unfinished-local-name";
        caller.Password = "unfinished-form-value";
        caller.RememberMe = true;
        var content = HelpCatalogLoader.Load().ForLanguage(AppLanguage.English);

        foreach (var topic in content.Topics.Values.Where(topic =>
                     topic.CategoryId is "security-tips" or "troubleshooting"))
        {
            if (topic.Id == HelpTopicIds.RememberMe)
                ExecuteCommand(shell.ShowRememberMeSecurityAdviceCommand);
            else
                shell.OpenHelpTopic(topic.Id);
            var page = (HelpPageViewModel)shell.CurrentPageViewModel;
            Assert.AreEqual(topic.Id, page.Route.ContentId);
            Assert.IsTrue(page.HasSections, topic.Id);
            Assert.IsFalse(page.IsTopicUnavailable, topic.Id);
            Assert.IsTrue(await shell.TryNavigateBackAsync());
            Assert.AreSame(caller, shell.CurrentPageViewModel);
            Assert.AreEqual("unfinished-local-name", caller.Username);
            Assert.AreEqual("unfinished-form-value", caller.Password);
            Assert.IsTrue(caller.RememberMe);
        }

        Assert.AreEqual(0, ((RejectAllEndpoints)(object)endpoints).Calls);
    }

    [TestMethod]
    public async Task RememberMeWarningsRefreshInAllThreeViewModelsWithoutChangingTheChoice()
    {
        await using var preferences = await TestUiPreferencesFactory.CreateAsync(
            resourceLoader: new ShippedLocalizationLoader());
        var endpoints = DispatchProxy.Create<IEndpoints, RejectAllEndpoints>();
        await using var runtime = new UnavailableRuntime();
        using var shell = new MainViewModel(endpoints, runtime, new FakeBackgroundSyncSettingsClient(), preferences);
        shell.LoginViewModel.RememberMe = true;
        shell.RegistrationViewModel.RememberMe = true;
        var notifications = new List<(ViewModelBase ViewModel, string? Property)>();
        foreach (var viewModel in new ViewModelBase[] { shell, shell.LoginViewModel, shell.RegistrationViewModel })
            viewModel.PropertyChanged += (_, args) => notifications.Add((viewModel, args.PropertyName));
        var english = shell.RememberMeWarningText;
        var englishAdviceLabel = shell.RememberMeWarningLabel;
        Assert.AreEqual(english, shell.LoginViewModel.RememberMeWarningText);
        Assert.AreEqual(english, shell.RegistrationViewModel.RememberMeWarningText);
        Assert.AreEqual(englishAdviceLabel, shell.LoginViewModel.RememberMeWarningLabel);
        Assert.AreEqual(englishAdviceLabel, shell.RegistrationViewModel.RememberMeWarningLabel);

        var result = await preferences.SetLanguageAsync(AppLanguage.Hungarian);

        Assert.IsTrue(result.IsSuccess);
        Assert.AreNotEqual(english, shell.RememberMeWarningText);
        Assert.AreNotEqual(englishAdviceLabel, shell.RememberMeWarningLabel);
        Assert.AreEqual(shell.RememberMeWarningText, shell.LoginViewModel.RememberMeWarningText);
        Assert.AreEqual(shell.RememberMeWarningText, shell.RegistrationViewModel.RememberMeWarningText);
        Assert.AreEqual(shell.RememberMeWarningLabel, shell.LoginViewModel.RememberMeWarningLabel);
        Assert.AreEqual(shell.RememberMeWarningLabel, shell.RegistrationViewModel.RememberMeWarningLabel);
        Assert.IsTrue(shell.LoginViewModel.RememberMe);
        Assert.IsTrue(shell.RegistrationViewModel.RememberMe);
        foreach (var viewModel in new ViewModelBase[] { shell, shell.LoginViewModel, shell.RegistrationViewModel })
            foreach (var property in new[] { "RememberMeWarningText", "RememberMeWarningLabel" })
                Assert.IsTrue(notifications.Contains((viewModel, property)), property);
        Assert.AreEqual(0, ((RejectAllEndpoints)(object)endpoints).Calls);
    }

    [TestMethod]
    public async Task RegistrationPrivacyWorksWithoutBackendAndPreservesAllFormValues()
    {
        await using var preferences = await TestUiPreferencesFactory.CreateAsync();
        var endpoints = DispatchProxy.Create<IEndpoints, RejectAllEndpoints>();
        await using var runtime = new UnavailableRuntime();
        using var shell = new MainViewModel(endpoints, runtime, new FakeBackgroundSyncSettingsClient(), preferences);
        ExecuteCommand(shell.ShowRegistrationCommand);
        var form = shell.RegistrationViewModel;
        form.Username = "unfinished-registration";
        form.FirstName = "First";
        form.LastName = "Last";
        form.Email = "local@example.test";
        form.Password = "unfinished-password";
        form.ConfirmPassword = "unfinished-password";
        form.RememberMe = true;
        // Isolate the policy gate from backend readiness without calling any endpoint.
        form.SetBackendInitialized(true);
        Assert.IsTrue(form.CanRegister, "A valid form must not require a prior policy visit.");
        form.SetBackendInitialized(false);
        var canRegister = form.CanRegister;

        ExecuteCommand(form.ShowPrivacyPolicyCommand);
        Assert.AreSame(shell.PrivacyPolicyViewModel, shell.CurrentPageViewModel);
        Assert.IsTrue(shell.IsInformationPageVisible);
        Assert.IsTrue(shell.PrivacyPolicyViewModel.Sections.Count > 0);
        Assert.IsTrue(await shell.TryNavigateBackAsync());

        Assert.AreSame(form, shell.CurrentPageViewModel);
        Assert.AreEqual("unfinished-registration", form.Username);
        Assert.AreEqual("First", form.FirstName);
        Assert.AreEqual("Last", form.LastName);
        Assert.AreEqual("local@example.test", form.Email);
        Assert.AreEqual("unfinished-password", form.Password);
        Assert.AreEqual("unfinished-password", form.ConfirmPassword);
        Assert.IsTrue(form.RememberMe);
        Assert.AreEqual(canRegister, form.CanRegister, "Reading must not change existing registration availability.");
        Assert.IsTrue(form.RestorePrivacyPolicyFocus);
        Assert.AreEqual(0, ((RejectAllEndpoints)(object)endpoints).Calls);
    }

    [TestMethod]
    public async Task SettingsPrivacyBackAndQuickReturnKeepTheOriginalCaller()
    {
        await using var preferences = await TestUiPreferencesFactory.CreateAsync();
        var endpoints = DispatchProxy.Create<IEndpoints, RejectAllEndpoints>();
        await using var runtime = new UnavailableRuntime();
        using var shell = new MainViewModel(endpoints, runtime, new FakeBackgroundSyncSettingsClient(), preferences);
        var caller = shell.CurrentPageViewModel;
        ExecuteCommand(shell.ShowSettingsCommand);
        ExecuteCommand(shell.SettingsViewModel.NavigateToPrivacyCommand);
        var policy = shell.PrivacyPolicyViewModel;
        policy.ScrollOffset = 150;
        Assert.AreSame(policy, shell.CurrentPageViewModel);
        Assert.IsTrue(await shell.TryNavigateBackAsync());
        Assert.AreSame(shell.SettingsViewModel, shell.CurrentPageViewModel);
        ExecuteCommand(shell.SettingsViewModel.NavigateToPrivacyCommand);
        Assert.AreSame(policy, shell.CurrentPageViewModel);
        Assert.AreEqual(150d, policy.ScrollOffset);
        ExecuteCommand(policy.ReturnToAppCommand);
        Assert.AreSame(caller, shell.CurrentPageViewModel);
        shell.OpenPrivacyPolicy();
        Assert.IsTrue(await shell.TryNavigateBackAsync());
        Assert.AreSame(caller, shell.CurrentPageViewModel, "A fresh visit must not inherit Settings history.");
        Assert.AreEqual(0, ((RejectAllEndpoints)(object)endpoints).Calls);
    }

    [TestMethod]
    public async Task PrivacyLanguageChangeRefreshesTheDocumentAndKeepsReadingPosition()
    {
        await using var preferences = await TestUiPreferencesFactory.CreateAsync(
            resourceLoader: new ShippedLocalizationLoader());
        using var page = new PrivacyPolicyViewModel(preferences, PrivacyPolicyCatalog.Load(), () => { }, () => { });
        var englishTitle = page.Title;
        var englishDate = page.DateText;
        var englishSections = page.Sections;
        page.ScrollOffset = 260;

        var result = await preferences.SetLanguageAsync(AppLanguage.Hungarian);

        Assert.IsTrue(result.IsSuccess);
        Assert.AreNotEqual(englishTitle, page.Title);
        Assert.AreNotEqual(englishDate, page.DateText);
        Assert.IsTrue(page.Title.StartsWith("Adatvédelmi tájékoztató", StringComparison.Ordinal));
        CollectionAssert.AreEqual(englishSections.Select(section => section.Id).ToArray(),
            page.Sections.Select(section => section.Id).ToArray());
        Assert.AreNotEqual(englishSections[0].Body, page.Sections[0].Body);
        Assert.AreEqual(260d, page.ScrollOffset);
    }

    public class RejectAllEndpoints : DispatchProxy
    {
        public int Calls { get; private set; }
        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
        {
            Calls++;
            throw new InvalidOperationException($"Documentation must not call endpoints: {targetMethod?.Name}");
        }
    }

    private static void ExecuteCommand(ICommand command) => command.Execute(null);

    private sealed class UnavailableRuntime : IBackendRuntimeClient
    {
        public BackendRuntimeSnapshot Snapshot { get; } = new(BackendRuntimeState.Failed,
            BackendRuntimeFailureKind.StartupFailure, null, DateTimeOffset.UtcNow);
        public event EventHandler<BackendRuntimeStateChangedEventArgs>? StateChanged { add { } remove { } }
        public Task ConnectAsync(CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task WaitUntilReadyAsync(CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task ResetDatabaseAndRestartAsync(CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class ShippedLocalizationLoader : IFrontendLocalizationResourceLoader
    {
        public ValueTask<Stream> OpenAsync(AppLanguage language, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return ValueTask.FromResult<Stream>(File.OpenRead(Path.Combine(AppContext.BaseDirectory,
                "Localization", language == AppLanguage.Hungarian ? "hu.json" : "en_us.json")));
        }
    }
}
