using Microsoft.VisualStudio.TestTools.UnitTesting;
using PasswordManagerLocal.Windows.Agent.Ui;
using PasswordManagerLocal.Windows.Ipc.Client;
using PasswordManagerLocal.Windows.Ipc.Contracts;
using PasswordManagerLocal.Windows.Tests.IPC.Infrastructure;

namespace PasswordManagerLocal.Windows.Tests.IPC.Ui;

[TestClass]
public sealed class WindowsUiOpenServiceTests
{
    [TestMethod]
    public async Task ActivationSuccessDoesNotLaunchUi()
    {
        var activation = Activation(UiActivationResultKind.Activated);
        var launcher = new FakeWindowsUiLauncher();
        var service = new WindowsUiOpenService(activation, launcher, AgentLocalizationTestFactory.CreateEnglish());

        var result = await service.OpenAsync(UiActivationReason.UserLaunch);

        Assert.AreEqual(UiOpenResultKind.Activated, result.Kind);
        Assert.AreEqual(0, launcher.LaunchCount);
    }

    [TestMethod]
    public async Task MissingActivationServerLaunchesUi()
    {
        var launcher = new FakeWindowsUiLauncher();
        var service = new WindowsUiOpenService(
            Activation(UiActivationResultKind.Unavailable),
            launcher,
            AgentLocalizationTestFactory.CreateEnglish());

        var result = await service.OpenAsync(UiActivationReason.TrayIcon);

        Assert.AreEqual(UiOpenResultKind.LaunchRequested, result.Kind);
        Assert.AreEqual(1, launcher.LaunchCount);
    }

    [TestMethod]
    public async Task ActivationProtocolFailureDoesNotLaunchDuplicateUi()
    {
        foreach (var kind in new[] { UiActivationResultKind.Rejected, UiActivationResultKind.Failed })
        {
            var launcher = new FakeWindowsUiLauncher();
            var service = new WindowsUiOpenService(Activation(kind), launcher, AgentLocalizationTestFactory.CreateEnglish());

            var result = await service.OpenAsync(UiActivationReason.UserLaunch);

            Assert.IsFalse(result.IsSuccess);
            Assert.AreEqual(0, launcher.LaunchCount);
        }
    }

    [TestMethod]
    public async Task ConcurrentOpenRequestsAreSerializedAndLaunchIsCoalesced()
    {
        var launcher = new FakeWindowsUiLauncher();
        var service = new WindowsUiOpenService(
            Activation(UiActivationResultKind.Unavailable),
            launcher,
            AgentLocalizationTestFactory.CreateEnglish(),
            launchCoalescingWindow: TimeSpan.FromMinutes(1));

        var results = await Task.WhenAll(
            service.OpenAsync(UiActivationReason.TrayIcon),
            service.OpenAsync(UiActivationReason.UserLaunch));

        Assert.IsTrue(results.All(result => result.Kind == UiOpenResultKind.LaunchRequested));
        Assert.AreEqual(1, launcher.LaunchCount);
    }

    [TestMethod]
    public async Task MissingExecutableAndLaunchFailureReturnSafeFailures()
    {
        foreach (var launchResult in new[]
        {
            new UiLaunchResult(UiLaunchResultKind.ExecutableNotFound, "Executable unavailable."),
            new UiLaunchResult(UiLaunchResultKind.LaunchFailed, "Launch failed.")
        })
        {
            var launcher = new FakeWindowsUiLauncher { Result = launchResult };
            var service = new WindowsUiOpenService(
                Activation(UiActivationResultKind.Unavailable),
                launcher,
                AgentLocalizationTestFactory.CreateEnglish());

            var result = await service.OpenAsync(UiActivationReason.UserLaunch);

            Assert.IsFalse(result.IsSuccess);
            Assert.AreEqual(1, launcher.LaunchCount);
        }
    }

    private static DelegateWindowsUiActivationClient Activation(UiActivationResultKind kind) =>
        new((_, _) => Task.FromResult(new UiActivationResult(kind, "Activation result.")));
}
