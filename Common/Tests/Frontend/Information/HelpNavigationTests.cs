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

        shell.OpenHelpTopic(HelpTopicIds.AddDevice);
        Assert.IsTrue(shell.IsInformationPageVisible);
        Assert.IsInstanceOfType<HelpPageViewModel>(shell.CurrentPageViewModel);
        Assert.IsTrue(await shell.TryNavigateBackAsync());
        Assert.AreSame(caller, shell.CurrentPageViewModel);
        Assert.AreEqual("unfinished-local-name", caller.Username);
        Assert.AreEqual("unfinished-form-value", caller.Password);

        shell.OpenHelpTopic("unknown/topic");
        var fallback = (HelpPageViewModel)shell.CurrentPageViewModel;
        Assert.AreEqual(InformationDestination.Help, fallback.Route.Destination);
        Assert.IsTrue(fallback.IsTopicUnavailable);
        Assert.AreEqual(4, fallback.Cards.Count);
        Assert.IsTrue(await shell.TryNavigateBackAsync());
        Assert.AreSame(caller, shell.CurrentPageViewModel);
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
            new InformationRoute(InformationDestination.HelpTopic, HelpTopicIds.AddDevice), () => { }, _ => { });
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
