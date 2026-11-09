using Microsoft.VisualStudio.TestTools.UnitTesting;
using PasswordManagerLocal.Windows.Frontend.Activation;

namespace PasswordManagerLocal.Windows.Tests.IPC.Activation;

[TestClass]
public sealed class WindowsStartupActivationBridgeTests
{
    [TestMethod]
    public async Task EarlyRequestsAreAcceptedAndCoalescedUntilPresentationIsReady()
    {
        var target = new RecordingBridge();
        using var bridge = new WindowsStartupActivationBridge(target);

        Assert.IsTrue(await bridge.ActivateAsync());
        Assert.IsTrue(await bridge.ActivateAsync());
        Assert.AreEqual(0, target.Calls);
        bridge.MarkPresentationReady();
        Assert.AreEqual(1, target.Calls);
        bridge.MarkPresentationReady();
        Assert.AreEqual(1, target.Calls);
    }

    [TestMethod]
    public async Task ClosingBeforePresentationDiscardsPendingActivation()
    {
        var target = new RecordingBridge();
        using var bridge = new WindowsStartupActivationBridge(target);
        Assert.IsTrue(await bridge.ActivateAsync());

        bridge.Stop();
        bridge.MarkPresentationReady();

        Assert.IsFalse(await bridge.ActivateAsync());
        Assert.AreEqual(0, target.Calls);
    }

    [TestMethod]
    public async Task StopCancelsAnActivationAlreadyQueuedToTheUiDispatcher()
    {
        var target = new RecordingBridge { WaitForCancellation = true };
        using var bridge = new WindowsStartupActivationBridge(target);
        bridge.MarkPresentationReady();
        var activation = bridge.ActivateAsync();
        Assert.AreEqual(1, target.Calls);
        Assert.IsFalse(activation.IsCompleted);

        bridge.Stop();

        Assert.IsFalse(await activation);
        Assert.IsTrue(target.LastToken.IsCancellationRequested);
    }

    private sealed class RecordingBridge : IWindowsWindowActivationBridge
    {
        public int Calls { get; private set; }
        public bool WaitForCancellation { get; init; }
        public CancellationToken LastToken { get; private set; }
        public async Task<bool> ActivateAsync(CancellationToken cancellationToken = default)
        {
            Calls++;
            LastToken = cancellationToken;
            if (WaitForCancellation)
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            return true;
        }
    }
}
