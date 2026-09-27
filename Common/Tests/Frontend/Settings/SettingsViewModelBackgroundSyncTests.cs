using Microsoft.VisualStudio.TestTools.UnitTesting;
using PasswordManagerLocal.Common.Contracts.Preferences;
using PasswordManagerLocal.Common.Frontend.Services;
using PasswordManagerLocal.Common.Frontend.ViewModels;
using PasswordManagerLocal.Common.Tests.Fakes;

namespace PasswordManagerLocal.Common.Tests.Frontend.Settings;

[TestClass]
public sealed class SettingsViewModelBackgroundSyncTests
{
    [TestMethod]
    public async Task SettingsPageLoadsAuthoritativeState()
    {
        var client = new FakeBackgroundSyncSettingsClient
        {
            State = FakeBackgroundSyncSettingsClient.Available(true)
        };
        var viewModel = CreateViewModel(client);

        await viewModel.LoadBackgroundSyncStateAsync();

        Assert.IsTrue(viewModel.IsBackgroundSyncEnabled);
        Assert.IsTrue(viewModel.IsBackgroundSyncControlVisible);
        Assert.IsTrue(viewModel.IsBackgroundSyncToggleEnabled);
        Assert.AreEqual(1, client.GetCount);
    }

    [TestMethod]
    public async Task ToggleIsDisabledWhileMutationIsActiveAndReenabledAfterCompletion()
    {
        var entered = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var client = new FakeBackgroundSyncSettingsClient
        {
            SetEntered = entered,
            SetRelease = release.Task
        };
        var viewModel = CreateViewModel(client);
        await viewModel.LoadBackgroundSyncStateAsync();

        viewModel.IsBackgroundSyncEnabled = true;
        await entered.Task;
        Assert.IsFalse(viewModel.IsBackgroundSyncToggleEnabled);
        Assert.IsTrue(viewModel.IsBackgroundSyncProgressVisible);

        release.TrySetResult();
        await WaitUntilAsync(() => client.SetCount == 1 && viewModel.IsBackgroundSyncToggleEnabled);
        Assert.IsTrue(viewModel.IsBackgroundSyncEnabled);
        Assert.IsFalse(viewModel.IsBackgroundSyncProgressVisible);
    }

    [TestMethod]
    public async Task ConclusiveFailureRestoresAuthoritativeStateAndReenablesToggle()
    {
        var client = new FakeBackgroundSyncSettingsClient
        {
            SetResult = new BackgroundSyncChangeResult(
                FakeBackgroundSyncSettingsClient.Available(false),
                WasOutcomeUncertain: false)
        };
        var viewModel = CreateViewModel(client);
        await viewModel.LoadBackgroundSyncStateAsync();

        viewModel.IsBackgroundSyncEnabled = true;
        await WaitUntilAsync(() => client.SetCount == 1 && viewModel.IsBackgroundSyncToggleEnabled);

        Assert.IsFalse(viewModel.IsBackgroundSyncEnabled);
        Assert.IsTrue(viewModel.IsBackgroundSyncToggleEnabled);
    }

    [TestMethod]
    public async Task UncertainOutcomeUsesReadBackStateAndDoesNotLeaveControlDisabled()
    {
        var client = new FakeBackgroundSyncSettingsClient
        {
            SetResult = new BackgroundSyncChangeResult(
                FakeBackgroundSyncSettingsClient.Available(true),
                WasOutcomeUncertain: true)
        };
        var viewModel = CreateViewModel(client);
        await viewModel.LoadBackgroundSyncStateAsync();

        viewModel.IsBackgroundSyncEnabled = true;
        await WaitUntilAsync(() => client.SetCount == 1 && viewModel.IsBackgroundSyncToggleEnabled);

        Assert.IsTrue(viewModel.IsBackgroundSyncEnabled);
        Assert.IsTrue(viewModel.IsBackgroundSyncToggleEnabled);
    }

    [TestMethod]
    public async Task AgentUnavailableDisplaysUnavailableStateAndDisablesToggle()
    {
        var client = new FakeBackgroundSyncSettingsClient
        {
            State = new BackgroundSyncClientState(
                IsEnabled: false,
                IsAvailable: false,
                IsDegraded: true,
                IsTransitionInProgress: false,
                BackgroundSyncClientFailureKind.Unavailable,
                SafeMessage: null)
        };
        var viewModel = CreateViewModel(client);

        await viewModel.LoadBackgroundSyncStateAsync();

        Assert.IsFalse(viewModel.IsBackgroundSyncControlVisible);
        Assert.IsFalse(viewModel.IsBackgroundSyncToggleEnabled);
        Assert.IsFalse(viewModel.IsBackgroundSyncEnabled);
        Assert.IsFalse(viewModel.IsBackgroundSyncProgressVisible);
    }

    private static SettingsViewModel CreateViewModel(
        FakeBackgroundSyncSettingsClient client) =>
        new(
            new UiPreferencesService(new InMemoryApplicationPreferencesStore(new ApplicationPreferences
            {
                SchemaVersion = ApplicationPreferences.CurrentSchemaVersion,
                Language = AppLanguage.English,
                Theme = AppThemeMode.Dark
            })),
            new DeviceAppPreferencesService(client),
            () => { });

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        while (!condition())
            await Task.Delay(10, timeout.Token);
    }
}
