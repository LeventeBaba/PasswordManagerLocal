using System.Reflection;
using System.Runtime.CompilerServices;
using System.Windows.Input;
using System.Xml.Linq;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using PasswordManagerLocal.Common.Contracts.Endpoints;
using PasswordManagerLocal.Common.Contracts.Preferences;
using PasswordManagerLocal.Common.Contracts.Responses;
using PasswordManagerLocal.Common.Contracts.Runtime;
using PasswordManagerLocal.Common.Frontend.Information;
using PasswordManagerLocal.Common.Frontend.Localization;
using PasswordManagerLocal.Common.Frontend.ViewModels;
using PasswordManagerLocal.Common.Frontend.ViewModels.Auth;
using PasswordManagerLocal.Common.Frontend.ViewModels.Pages;
using PasswordManagerLocal.Common.Tests.Fakes;

namespace PasswordManagerLocal.Common.Tests.Frontend.Information;

[TestClass]
public sealed class ContextHelpTests
{
    [TestMethod]
    public async Task EveryContextOpensOfflineWithoutReplacingOrResettingTheRegistrationForm()
    {
        await using var preferences = await TestUiPreferencesFactory.CreateAsync();
        var endpoints = DispatchProxy.Create<IEndpoints, RejectAllEndpoints>();
        await using var runtime = new UnavailableRuntime();
        using var shell = new MainViewModel(endpoints, runtime, new FakeBackgroundSyncSettingsClient(), preferences);
        Execute(shell.ShowRegistrationCommand);
        var form = shell.RegistrationViewModel;
        form.Username = "unfinished-profile";
        form.Email = "local@example.test";
        form.FirstName = "First";
        form.LastName = "Last";
        form.Password = "unfinished-master-password";
        form.ConfirmPassword = form.Password;
        form.RememberMe = true;

        foreach (var topic in new[]
                 {
                     HelpTopicIds.CreateAccount, HelpTopicIds.MasterPassword, HelpTopicIds.SignIn,
                     HelpTopicIds.LoginProblems, HelpTopicIds.EnrollmentOverview, HelpTopicIds.EnrollmentCodeSafety,
                     HelpTopicIds.EnrollmentProblems, HelpTopicIds.AddDevice, HelpTopicIds.Disconnect,
                     HelpTopicIds.DeleteAccount, HelpTopicIds.Blocked, HelpTopicIds.SyncProblems,
                     HelpTopicIds.Diagnostics, HelpTopicIds.TransferBetweenProfiles, HelpTopicIds.MoveVsCopy,
                     HelpTopicIds.SwitchProfiles
                 })
        {
            shell.OpenContextHelp(topic);
            Assert.AreSame(form, shell.CurrentPageViewModel);
            Assert.IsTrue(shell.IsContextHelpOpen);
            Assert.IsFalse(shell.IsApplicationSurfaceEnabled);
            Assert.AreEqual(topic, shell.ContextHelpPage!.Route.ContentId);
            Assert.IsTrue(shell.ContextHelpPage.HasSections);
            Assert.IsTrue(await shell.TryNavigateBackAsync());
            Assert.IsFalse(shell.IsContextHelpOpen);
            Assert.IsTrue(shell.IsApplicationSurfaceEnabled);
            Assert.AreSame(form, shell.CurrentPageViewModel);
        }
        Assert.AreEqual("unfinished-profile", form.Username);
        Assert.AreEqual("local@example.test", form.Email);
        Assert.AreEqual("First", form.FirstName);
        Assert.AreEqual("Last", form.LastName);
        Assert.AreEqual("unfinished-master-password", form.Password);
        Assert.AreEqual(form.Password, form.ConfirmPassword);
        Assert.IsTrue(form.RememberMe);
        Assert.AreEqual(0, ((RejectAllEndpoints)(object)endpoints).Calls);
    }

    [TestMethod]
    public async Task RelatedTopicsHaveIndependentBackHistoryAndQuickReturnDoesNotNavigateTheTask()
    {
        await using var preferences = await TestUiPreferencesFactory.CreateAsync();
        var endpoints = DispatchProxy.Create<IEndpoints, RejectAllEndpoints>();
        await using var runtime = new UnavailableRuntime();
        using var shell = new MainViewModel(endpoints, runtime, new FakeBackgroundSyncSettingsClient(), preferences);
        Execute(shell.ShowSettingsCommand);
        shell.OpenContextHelp(HelpTopicIds.AddDevice);
        var original = shell.ContextHelpPage!;
        original.ScrollOffset = 190;
        var related = original.Cards.First();
        original.FocusedCardId = related.Id;
        Execute(related.OpenCommand);
        Assert.AreEqual(related.Id, shell.ContextHelpPage!.Route.ContentId);
        Assert.IsTrue(await shell.TryNavigateBackAsync());
        Assert.AreSame(original, shell.ContextHelpPage);
        Assert.AreEqual(190d, original.ScrollOffset);
        Assert.AreEqual(related.Id, original.FocusedCardId);
        Execute(original.ReturnToAppCommand);
        Assert.AreSame(shell.SettingsViewModel, shell.CurrentPageViewModel);
        Assert.IsTrue(await shell.TryNavigateBackAsync());
        Assert.AreSame(shell.LoginViewModel, shell.CurrentPageViewModel);

        shell.OpenContextHelp("missing/topic");
        Assert.IsTrue(shell.ContextHelpPage!.IsTopicUnavailable);
        Assert.IsTrue(shell.ContextHelpPage.IsHelpHome);
        Assert.IsTrue(await shell.TryNavigateBackAsync());
        Assert.IsFalse(shell.IsContextHelpOpen);
        Assert.AreSame(shell.LoginViewModel, shell.CurrentPageViewModel);
        Assert.AreEqual(0, ((RejectAllEndpoints)(object)endpoints).Calls);
    }

    [TestMethod]
    public async Task ExportConfirmationCannotRunThroughHelpAndRetainsItsTargetItemsAndMoveChoice()
    {
        await using var preferences = await TestUiPreferencesFactory.CreateAsync();
        var endpoints = DispatchProxy.Create<IEndpoints, RejectAllEndpoints>();
        await using var runtime = new UnavailableRuntime();
        using var shell = new MainViewModel(endpoints, runtime, new FakeBackgroundSyncSettingsClient(), preferences);
        var passwords = shell.PasswordsViewModel;
        var target = Guid.NewGuid();
        var item = Guid.NewGuid();
        // Arrange a pending, unsubmitted UI confirmation without issuing a backend request.
        SetField(passwords, "_selectedExportTargetToken", target);
        var kind = typeof(PasswordsViewModel).GetField("_pendingExportKind", BindingFlags.Instance | BindingFlags.NonPublic)!;
        kind.SetValue(passwords, Enum.Parse(kind.FieldType, "Passwords"));
        var items = new[] { item };
        SetField(passwords, "_pendingExportItemIds", items);
        SetProperty(passwords, nameof(passwords.IsExportConfirmationOpen), true);
        passwords.DeleteOriginalOnExport = true;
        SetProperty(shell, nameof(shell.CurrentPageViewModel), passwords);
        Assert.IsTrue(shell.HasConfirmableDialogOpen);

        shell.OpenContextHelp(HelpTopicIds.MoveVsCopy);
        Assert.IsFalse(shell.HasConfirmableDialogOpen);
        await shell.ConfirmOpenDialogAsync();
        Assert.AreEqual(0, ((RejectAllEndpoints)(object)endpoints).Calls);
        Assert.IsTrue(passwords.IsExportConfirmationOpen);
        Assert.IsTrue(passwords.DeleteOriginalOnExport);
        Assert.IsTrue(await shell.TryNavigateBackAsync());

        Assert.AreSame(passwords, shell.CurrentPageViewModel);
        Assert.IsTrue(shell.HasConfirmableDialogOpen);
        Assert.IsTrue(passwords.IsExportConfirmationOpen);
        Assert.IsTrue(passwords.DeleteOriginalOnExport);
        Assert.AreEqual(target, typeof(PasswordsViewModel).GetField("_selectedExportTargetToken", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(passwords));
        Assert.AreSame(items, typeof(PasswordsViewModel).GetField("_pendingExportItemIds", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(passwords));
        CollectionAssert.AreEqual(new[] { item }, items);
    }

    [TestMethod]
    public async Task DeleteDisconnectAndAddDeviceHelpDoNotClearSensitiveOrUnsubmittedValues()
    {
        await using var preferences = await TestUiPreferencesFactory.CreateAsync();
        var endpoints = DispatchProxy.Create<IEndpoints, RejectAllEndpoints>();
        await using var runtime = new UnavailableRuntime();
        using var shell = new MainViewModel(endpoints, runtime, new FakeBackgroundSyncSettingsClient(), preferences);
        var profile = shell.ProfileViewModel;
        SetProperty(shell, nameof(shell.CurrentPageViewModel), profile);
        Execute(profile.BeginDeleteAccountCommand);
        profile.DeleteAccountPassword = "unsubmitted-delete-password";
        var deletePane = profile.CurrentProfilePane;
        shell.OpenContextHelp(HelpTopicIds.DeleteAccount);
        Assert.IsTrue(await shell.TryNavigateBackAsync());
        Assert.AreEqual(deletePane, profile.CurrentProfilePane);
        Assert.AreEqual("unsubmitted-delete-password", profile.DeleteAccountPassword);

        var device = CreateDevice(profile, new UserDeviceInfoResponse
        {
            DeviceId = Guid.NewGuid(), Name = "Other device", IsTrusted = true,
            IsSyncOn = true, IsOnline = true
        });
        Execute(profile.DisconnectDeviceCommand, device);
        profile.DisconnectDevicePassword = "unsubmitted-disconnect-password";
        var disconnectPane = profile.CurrentDevicePane;
        shell.OpenContextHelp(HelpTopicIds.Disconnect);
        Assert.IsTrue(await shell.TryNavigateBackAsync());
        Assert.AreSame(device, profile.DeviceToDisconnect);
        Assert.AreEqual(disconnectPane, profile.CurrentDevicePane);
        Assert.AreEqual("unsubmitted-disconnect-password", profile.DisconnectDevicePassword);

        profile.DeviceEnrollmentCodeInput = "unfinished-enrollment-input";
        shell.OpenContextHelp(HelpTopicIds.AddDevice);
        Execute(shell.ContextHelpPage!.ReturnToAppCommand);
        Assert.AreEqual("unfinished-enrollment-input", profile.DeviceEnrollmentCodeInput);
        Assert.AreEqual(0, ((RejectAllEndpoints)(object)endpoints).Calls);
    }

    [TestMethod]
    public async Task EnrollmentCodeAndDeadlineRemainActiveWhileHelpIsOpen()
    {
        await using var preferences = await TestUiPreferencesFactory.CreateAsync();
        var endpoints = DispatchProxy.Create<IEndpoints, RejectAllEndpoints>();
        var scheduler = new ManualFrontendDeadlineScheduler();
        scheduler.AdvanceTo(DateTimeOffset.UtcNow);
        await using var runtime = new UnavailableRuntime();
        using var shell = new MainViewModel(endpoints, runtime, new FakeBackgroundSyncSettingsClient(), preferences,
            deadlineScheduler: scheduler);
        var login = shell.LoginViewModel;
        SetField(login, "_isDeviceTransferCodeVisible", true);
        SetField(login, "_deviceTransferCode", "unshared-enrollment-code");
        typeof(LoginViewModel).GetMethod("ScheduleEnrollmentExpiration", BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(login, [scheduler.NowUtc.AddMinutes(5)]);
        Assert.AreEqual(1, scheduler.ActiveCount);

        shell.OpenContextHelp(HelpTopicIds.EnrollmentCodeSafety);
        Assert.AreSame(login, shell.CurrentPageViewModel);
        Assert.IsTrue(login.IsDeviceTransferCodeVisible);
        Assert.AreEqual("unshared-enrollment-code", login.DeviceTransferCode);
        Assert.AreEqual(1, scheduler.ActiveCount, "Reading help must not cancel or pause enrollment expiry.");
        Assert.IsTrue(await shell.TryNavigateBackAsync());
        Assert.IsTrue(login.IsDeviceTransferCodeVisible);
        Assert.AreEqual(1, scheduler.ActiveCount);
        Assert.AreEqual(0, ((RejectAllEndpoints)(object)endpoints).Calls);
    }

    [TestMethod]
    public async Task LanguageRefreshAndLogoutKeepThePublicOverlayIndependentOfSessionState()
    {
        await using var preferences = await TestUiPreferencesFactory.CreateAsync(resourceLoader: new ShippedLocalizationLoader());
        var endpoints = DispatchProxy.Create<IEndpoints, RejectAllEndpoints>();
        await using var runtime = new UnavailableRuntime();
        using var shell = new MainViewModel(endpoints, runtime, new FakeBackgroundSyncSettingsClient(), preferences);
        shell.OpenContextHelp(HelpTopicIds.CreateAccount);
        var page = shell.ContextHelpPage!;
        var english = page.Title;
        var englishLabel = shell.RegistrationViewModel.RegistrationHelpLabel;
        page.ScrollOffset = 220;
        Assert.IsTrue((await preferences.SetLanguageAsync(AppLanguage.Hungarian)).IsSuccess);
        Assert.AreSame(page, shell.ContextHelpPage);
        Assert.AreNotEqual(english, page.Title);
        Assert.AreNotEqual(englishLabel, shell.RegistrationViewModel.RegistrationHelpLabel);
        Assert.AreEqual(220d, page.ScrollOffset);
        await shell.RequestLogoutAsync();
        Assert.AreSame(page, shell.ContextHelpPage);
        Execute(page.ReturnToAppCommand);
        Assert.IsFalse(shell.IsContextHelpOpen);
        Assert.AreSame(shell.LoginViewModel, shell.CurrentPageViewModel);
        Assert.AreEqual(0, ((RejectAllEndpoints)(object)endpoints).Calls);
    }

    [TestMethod]
    public async Task DeviceProblemShortcutFollowsExistingNonSensitiveStatusAndSyncToggleChanges()
    {
        await using var preferences = await TestUiPreferencesFactory.CreateAsync();
        var endpoints = DispatchProxy.Create<IEndpoints, RejectAllEndpoints>();
        using var owner = new ProfileViewModel(preferences, endpoints, () => Task.FromResult(true), () => Task.CompletedTask);
        var response = new UserDeviceInfoResponse { IsOnline = true, IsTrusted = true, IsSyncOn = true };
        var healthy = CreateDevice(owner, response);
        Assert.IsFalse(healthy.ShowSyncHelp);
        var changed = new List<string?>();
        healthy.PropertyChanged += (_, e) => changed.Add(e.PropertyName);
        healthy.ApplySyncState(false);
        Assert.IsTrue(healthy.ShowSyncHelp);
        CollectionAssert.Contains(changed, nameof(DeviceItemViewModel.ShowSyncHelp));
        healthy.ApplySyncState(true);
        Assert.IsFalse(healthy.ShowSyncHelp);
        response.IsOnline = false;
        Assert.IsTrue(CreateDevice(owner, response).ShowSyncHelp);
        response.IsBlocked = true;
        Assert.IsFalse(CreateDevice(owner, response).ShowSyncHelp, "Blocked devices have their own specific guide.");
        response.IsBlocked = false;
        response.IsCurrentDevice = true;
        Assert.IsFalse(CreateDevice(owner, response).ShowSyncHelp);
    }

    [TestMethod]
    public void AllNineScreensUseRealNamedTopicsAndHelpActionsCannotSubmitCommands()
    {
        var root = GetRepositoryRoot();
        XNamespace controls = "clr-namespace:PasswordManagerLocal.Common.Frontend.Views.Controls";
        var expected = new Dictionary<string, string[]>
        {
            ["Auth/RegistrationView.axaml"] = [nameof(HelpTopicIds.CreateAccount), nameof(HelpTopicIds.MasterPassword)],
            ["Auth/LoginView.axaml"] = [nameof(HelpTopicIds.SignIn), nameof(HelpTopicIds.LoginProblems), nameof(HelpTopicIds.EnrollmentOverview), nameof(HelpTopicIds.EnrollmentCodeSafety), nameof(HelpTopicIds.EnrollmentProblems)],
            ["Auth/ChangeProfileView.axaml"] = [nameof(HelpTopicIds.SwitchProfiles)],
            ["Pages/Profile/DeviceAddView.axaml"] = [nameof(HelpTopicIds.AddDevice)],
            ["Pages/Profile/DeviceDisconnectView.axaml"] = [nameof(HelpTopicIds.Disconnect)],
            ["Pages/Profile/ProfileDeleteAccountView.axaml"] = [nameof(HelpTopicIds.DeleteAccount)],
            ["Pages/Profile/DeviceDetailsView.axaml"] = [nameof(HelpTopicIds.Blocked), nameof(HelpTopicIds.SyncProblems), nameof(HelpTopicIds.Diagnostics)],
            ["Pages/Passwords/ExportTargetSelectionView.axaml"] = [nameof(HelpTopicIds.TransferBetweenProfiles)],
            ["Pages/Passwords/ExportConfirmationDialogView.axaml"] = [nameof(HelpTopicIds.MoveVsCopy)]
        };
        var catalog = HelpCatalogLoader.Load();
        foreach (var (path, topics) in expected)
        {
            var buttons = XDocument.Load(Path.Combine(root, "Common", "Frontend", "Views", path))
                .Descendants(controls + "ContextHelpButton").ToArray();
            foreach (var topic in topics)
                Assert.IsTrue(buttons.Any(button => (string?)button.Attribute("TopicId") == $"{{x:Static info:HelpTopicIds.{topic}}}"), $"{path}: {topic}");
            foreach (var button in buttons)
            {
                Assert.IsNull(button.Attribute("Command"), "A help button must not execute a task command.");
                Assert.IsNull(button.Attribute("CommandParameter"), "Help must not carry account/form data.");
                Assert.IsNotNull(button.Attribute("AutomationProperties.Name"));
                Assert.IsNotNull(button.Descendants().FirstOrDefault(element => element.Name.LocalName == "TextBlock"));
            }
            foreach (var topic in topics)
            {
                var id = (string)typeof(HelpTopicIds).GetField(topic)!.GetRawConstantValue()!;
                Assert.IsTrue(catalog.ForLanguage(AppLanguage.English).Topics.ContainsKey(id));
                Assert.IsTrue(catalog.ForLanguage(AppLanguage.Hungarian).Topics.ContainsKey(id));
            }
        }
    }

    private static DeviceItemViewModel CreateDevice(ProfileViewModel owner, UserDeviceInfoResponse response) =>
        (DeviceItemViewModel)typeof(ProfileViewModel).GetMethod("CreateDeviceItem", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(owner, [response])!;

    private static void Execute(ICommand command, object? parameter = null) => command.Execute(parameter);
    private static void SetProperty(object target, string name, object value) =>
        target.GetType().GetProperty(name, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)!.SetValue(target, value);
    private static void SetField(object target, string name, object value) =>
        target.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(target, value);

    private static string GetRepositoryRoot([CallerFilePath] string sourcePath = "") =>
        Path.GetFullPath(Path.Combine(Path.GetDirectoryName(sourcePath)!, "..", "..", "..", ".."));

    public class RejectAllEndpoints : DispatchProxy
    {
        public int Calls { get; private set; }
        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
        {
            Calls++;
            throw new InvalidOperationException($"Help must not call endpoints: {targetMethod?.Name}");
        }
    }

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
            return ValueTask.FromResult<Stream>(File.OpenRead(Path.Combine(AppContext.BaseDirectory, "Localization",
                language == AppLanguage.Hungarian ? "hu.json" : "en_us.json")));
        }
    }
}
