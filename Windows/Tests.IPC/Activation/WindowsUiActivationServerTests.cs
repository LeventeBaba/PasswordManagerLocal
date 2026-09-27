using Microsoft.VisualStudio.TestTools.UnitTesting;
using PasswordManagerLocal.Windows.Frontend.Activation;
using PasswordManagerLocal.Windows.Ipc.Contracts;
using PasswordManagerLocal.Windows.Tests.IPC.Infrastructure;

namespace PasswordManagerLocal.Windows.Tests.IPC.Activation;

[TestClass]
public sealed class WindowsUiActivationServerTests
{
    [TestMethod]
    public async Task OrdinaryActivationDoesNotInstallShutdownSuppression()
    {
        var activation = new FakeWindowsWindowActivationBridge();
        var shutdown = new FakeWindowsUiShutdownBridge();
        var coordinator = new FakeIntentionalAgentShutdownCoordinator();
        var sink = new WindowsUiActivationRequestSink(activation, shutdown, coordinator);

        var accepted = await sink.RequestActivationAsync(
            new UiActivationRequestDto(
                UiActivationReason.TrayIcon,
                BringToForeground: true,
                UiActivationCommand.Activate),
            CancellationToken.None);

        Assert.IsTrue(accepted);
        Assert.AreEqual(1, activation.ActivationCount);
        Assert.AreEqual(0, coordinator.BeginCount);
        Assert.AreEqual(0, shutdown.ShutdownCount);
    }

    [TestMethod]
    public async Task IntentionalShutdownInstallsSuppressionBeforeSchedulingUiShutdown()
    {
        var activation = new FakeWindowsWindowActivationBridge();
        var shutdown = new FakeWindowsUiShutdownBridge();
        var coordinator = new FakeIntentionalAgentShutdownCoordinator();
        var sink = new WindowsUiActivationRequestSink(activation, shutdown, coordinator);

        var accepted = await sink.RequestActivationAsync(
            CreateShutdownRequest(),
            CancellationToken.None);

        Assert.IsTrue(accepted);
        Assert.AreEqual(1, coordinator.BeginCount);
        Assert.AreEqual(1, shutdown.ShutdownCount);
        Assert.AreEqual(0, coordinator.CancelCount);
        Assert.AreEqual(0, activation.ActivationCount);
    }

    [TestMethod]
    public async Task RepeatedIntentionalShutdownRequestsAreIdempotent()
    {
        var shutdown = new FakeWindowsUiShutdownBridge();
        var coordinator = new FakeIntentionalAgentShutdownCoordinator();
        var sink = new WindowsUiActivationRequestSink(
            new FakeWindowsWindowActivationBridge(), shutdown, coordinator);

        var first = await sink.RequestActivationAsync(CreateShutdownRequest(), CancellationToken.None);
        var second = await sink.RequestActivationAsync(CreateShutdownRequest(), CancellationToken.None);

        Assert.IsTrue(first);
        Assert.IsTrue(second);
        Assert.AreEqual(1, coordinator.BeginCount);
        Assert.AreEqual(1, shutdown.ShutdownCount);
    }

    [TestMethod]
    public async Task FailedUiSchedulingCancelsSuppressionAndAllowsRetry()
    {
        var shutdown = new FakeWindowsUiShutdownBridge { Result = false };
        var coordinator = new FakeIntentionalAgentShutdownCoordinator();
        var sink = new WindowsUiActivationRequestSink(
            new FakeWindowsWindowActivationBridge(), shutdown, coordinator);

        var first = await sink.RequestActivationAsync(CreateShutdownRequest(), CancellationToken.None);
        shutdown.Result = true;
        var second = await sink.RequestActivationAsync(CreateShutdownRequest(), CancellationToken.None);

        Assert.IsFalse(first);
        Assert.IsTrue(second);
        Assert.AreEqual(2, coordinator.BeginCount);
        Assert.AreEqual(1, coordinator.CancelCount);
        Assert.AreEqual(2, shutdown.ShutdownCount);
    }

    [TestMethod]
    public async Task DisposalStopsActivationServerAcceptance()
    {
        var host = new FakeWindowsIpcServerHost();
        var server = new WindowsUiActivationServer(host);
        await server.StartAsync();

        await server.DisposeAsync();

        Assert.AreEqual(1, host.StartCount);
        Assert.AreEqual(1, host.StopCount);
        Assert.AreEqual(1, host.DisposeCount);
    }

    private static UiActivationRequestDto CreateShutdownRequest() => new(
        UiActivationReason.AgentRequest,
        BringToForeground: false,
        UiActivationCommand.IntentionalAgentShutdown);
}
