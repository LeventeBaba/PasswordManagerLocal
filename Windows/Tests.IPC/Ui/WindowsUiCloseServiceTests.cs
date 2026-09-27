using Microsoft.VisualStudio.TestTools.UnitTesting;
using PasswordManagerLocal.Windows.Agent.Ui;
using PasswordManagerLocal.Windows.Ipc.Client;
using PasswordManagerLocal.Windows.Ipc.Contracts;
using PasswordManagerLocal.Windows.Tests.IPC.Infrastructure;

namespace PasswordManagerLocal.Windows.Tests.IPC.Ui;

[TestClass]
public sealed class WindowsUiCloseServiceTests
{
    [TestMethod]
    public async Task AcknowledgedResultMeansIntentionalShutdownCommandWasAccepted()
    {
        var client = new FakeWindowsUiActivationClient();
        var service = new WindowsUiCloseService(client, AgentLocalizationTestFactory.CreateEnglish(), TimeSpan.FromSeconds(1));

        var result = await service.RequestIntentionalShutdownAsync();

        Assert.AreEqual(WindowsUiCloseResultKind.Acknowledged, result.Kind);
        Assert.IsTrue(result.IsAcknowledged);
        Assert.AreEqual(UiActivationCommand.IntentionalAgentShutdown, client.LastRequest!.Command);
        Assert.IsFalse(client.LastRequest.BringToForeground);
    }

    [TestMethod]
    public async Task RejectionRemainsExplicit()
    {
        var client = new FakeWindowsUiActivationClient
        {
            Result = new UiActivationResult(UiActivationResultKind.Rejected, "rejected")
        };
        var service = new WindowsUiCloseService(client, AgentLocalizationTestFactory.CreateEnglish(), TimeSpan.FromSeconds(1));

        var result = await service.RequestIntentionalShutdownAsync();

        Assert.AreEqual(WindowsUiCloseResultKind.Rejected, result.Kind);
        Assert.IsFalse(result.IsAcknowledged);
    }

    [TestMethod]
    public async Task MissingAcknowledgementReturnsBoundedFailure()
    {
        var client = new FakeWindowsUiActivationClient
        {
            Completion = new TaskCompletionSource<UiActivationResult>(
                TaskCreationOptions.RunContinuationsAsynchronously)
        };
        var service = new WindowsUiCloseService(client, AgentLocalizationTestFactory.CreateEnglish(), TimeSpan.FromMilliseconds(25));

        var result = await service.RequestIntentionalShutdownAsync();

        Assert.AreEqual(WindowsUiCloseResultKind.Failed, result.Kind);
        StringAssert.Contains(result.SafeMessage, "timed out");
    }

    [TestMethod]
    public async Task CallerCancellationPropagates()
    {
        var client = new FakeWindowsUiActivationClient
        {
            Completion = new TaskCompletionSource<UiActivationResult>(
                TaskCreationOptions.RunContinuationsAsynchronously)
        };
        var service = new WindowsUiCloseService(client, AgentLocalizationTestFactory.CreateEnglish(), TimeSpan.FromSeconds(1));
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        await Assert.ThrowsExactlyAsync<OperationCanceledException>(
            () => service.RequestIntentionalShutdownAsync(cancellation.Token));
    }
}
