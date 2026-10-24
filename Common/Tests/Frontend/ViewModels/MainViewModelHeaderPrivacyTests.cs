using System.Reflection;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using PasswordManagerLocal.Common.Contracts.Endpoints;
using PasswordManagerLocal.Common.Contracts.Preferences;
using PasswordManagerLocal.Common.Contracts.Responses;
using PasswordManagerLocal.Common.Contracts.Runtime;
using PasswordManagerLocal.Common.Frontend.Localization;
using PasswordManagerLocal.Common.Frontend.ViewModels;
using PasswordManagerLocal.Common.Tests.Fakes;

namespace PasswordManagerLocal.Common.Tests.Frontend.ViewModels;

[TestClass]
public sealed class MainViewModelHeaderPrivacyTests
{
    [TestMethod]
    [DataRow("private-login-name")]
    [DataRow("")]
    public async Task IdleHeaderDoesNotRevealUsernameOrFallbackEmailAfterRememberedSignIn(string username)
    {
        await using var preferences = await TestUiPreferencesFactory.CreateAsync(
            resourceLoader: new ShippedLocalizationLoader());
        var endpoints = DispatchProxy.Create<IEndpoints, HeaderEndpoints>();
        var recording = (HeaderEndpoints)(object)endpoints;
        recording.Profile.Username = username;
        await using var runtime = new ReadyRuntime();
        using var shell = new MainViewModel(endpoints, runtime, new FakeBackgroundSyncSettingsClient(), preferences,
            deadlineScheduler: new ManualFrontendDeadlineScheduler());
        var initialization = shell.InitializeAsync();
        runtime.Ready.TrySetResult();

        await initialization;
        Assert.IsTrue(shell.IsAuthenticated);
        shell.ClearStatusMessage();

        Assert.AreEqual("Signed in", shell.HeaderSubtitle);
        Assert.IsTrue(shell.IsHeaderSubtitleIdle);
        Assert.IsTrue(shell.HasHeaderSubtitle);
        Assert.IsFalse(shell.HeaderSubtitle.Contains("private-login-name", StringComparison.Ordinal));
        Assert.IsFalse(shell.HeaderSubtitle.Contains(recording.Profile.Email, StringComparison.Ordinal));
        Assert.AreEqual(username, shell.ProfileViewModel.Username,
            "The explicit account view still needs the actual username.");
    }

    [TestMethod]
    public async Task HeaderKeepsMessagePriorityAndLocalizesIdleStatusWithoutKeepingItAfterLogout()
    {
        await using var preferences = await TestUiPreferencesFactory.CreateAsync(
            resourceLoader: new ShippedLocalizationLoader());
        var endpoints = DispatchProxy.Create<IEndpoints, HeaderEndpoints>();
        await using var runtime = new ReadyRuntime();
        using var shell = new MainViewModel(endpoints, runtime, new FakeBackgroundSyncSettingsClient(), preferences,
            deadlineScheduler: new ManualFrontendDeadlineScheduler());
        var initialization = shell.InitializeAsync();
        Assert.AreEqual("Initializing services...", shell.HeaderSubtitle);
        Assert.IsTrue(shell.IsHeaderSubtitleInformation);
        Assert.IsFalse(shell.IsHeaderSubtitleIdle);

        runtime.Ready.TrySetResult();
        await initialization;
        Assert.AreEqual("Remembered session restored.", shell.HeaderSubtitle);
        Assert.IsTrue(shell.IsHeaderSubtitleSuccess);
        Assert.IsFalse(shell.IsHeaderSubtitleIdle);
        shell.ClearStatusMessage();
        Assert.AreEqual("Signed in", shell.HeaderSubtitle);

        var languageChange = await preferences.SetLanguageAsync(AppLanguage.Hungarian);
        Assert.IsTrue(languageChange.IsSuccess);
        Assert.AreEqual("Bejelentkezve", shell.HeaderSubtitle);

        // The completed failing endpoint exercises the real notification path.
        shell.IsCurrentRememberMeEnabled = false;
        Assert.IsTrue(shell.IsHeaderSubtitleError);
        Assert.IsFalse(shell.IsHeaderSubtitleIdle);
        Assert.AreEqual(preferences.GetString("Shell_RememberMeUpdateFailed"), shell.HeaderSubtitle);
        shell.ClearStatusMessage();
        Assert.AreEqual("Bejelentkezve", shell.HeaderSubtitle);

        await shell.RequestLogoutAsync();
        shell.ClearStatusMessage();
        Assert.IsFalse(shell.IsAuthenticated);
        Assert.IsFalse(shell.IsHeaderSubtitleIdle);
        Assert.IsFalse(shell.HasHeaderSubtitle);
    }

    public class HeaderEndpoints : DispatchProxy
    {
        private readonly Guid _token = Guid.NewGuid();
        public UserProfileInfoResponse Profile { get; } = new()
        {
            UId = Guid.NewGuid(), Username = "private-login-name", Email = "private-email@example.test",
            FirstName = "Example", LastName = "Person", IsRememberMeEnabled = true
        };

        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args) => targetMethod?.Name switch
        {
            nameof(IEndpoints.RestoreRememberedSessionsAsync) => Task.FromResult<IReadOnlyList<Guid>>([_token]),
            nameof(IEndpoints.GetAuthSessionStatusAsync) => Task.FromResult(new AuthSessionStatusResponse { IsAuthenticated = true }),
            nameof(IEndpoints.GetUserProfileInfoAsync) => Task.FromResult(Profile),
            nameof(IEndpoints.GetSavedPasswordsAsync) => Task.FromResult(new SavedPasswordsResponse()),
            nameof(IEndpoints.SetRememberMeAsync) => Task.FromException(new InvalidOperationException()),
            nameof(IEndpoints.LogoutAsync) => Task.CompletedTask,
            _ => throw new InvalidOperationException($"Unexpected endpoint: {targetMethod?.Name}")
        };
    }

    private sealed class ReadyRuntime : IBackendRuntimeClient
    {
        public TaskCompletionSource Ready { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public BackendRuntimeSnapshot Snapshot { get; } = new(BackendRuntimeState.Ready,
            BackendRuntimeFailureKind.None, null, DateTimeOffset.UtcNow);
        public event EventHandler<BackendRuntimeStateChangedEventArgs>? StateChanged { add { } remove { } }
        public Task ConnectAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task WaitUntilReadyAsync(CancellationToken cancellationToken = default) => Ready.Task;
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
