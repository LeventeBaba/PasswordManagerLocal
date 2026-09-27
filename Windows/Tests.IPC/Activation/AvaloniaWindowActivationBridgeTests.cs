using Microsoft.VisualStudio.TestTools.UnitTesting;
using PasswordManagerLocal.Windows.Frontend.Activation;
using PasswordManagerLocal.Windows.Tests.IPC.Infrastructure;

namespace PasswordManagerLocal.Windows.Tests.IPC.Activation;

[TestClass]
public sealed class AvaloniaWindowActivationBridgeTests
{
    [TestMethod]
    public async Task ActivationIsMarshaledRestoresShowsActivatesAndFocuses()
    {
        var dispatcher = new RecordingWindowsUiDispatcher();
        var target = new FakeWindowsWindowActivationTarget(() => dispatcher.IsInvoking)
        {
            IsMinimized = true,
            IsVisible = false
        };
        var provider = new FakeWindowsWindowActivationTargetProvider { Target = target };
        var bridge = new AvaloniaWindowActivationBridge(dispatcher, provider);

        var activated = await bridge.ActivateAsync();

        Assert.IsTrue(activated);
        Assert.AreEqual(1, dispatcher.InvokeCount);
        Assert.AreEqual(1, target.RestoreCount);
        Assert.AreEqual(1, target.ShowCount);
        Assert.AreEqual(1, target.ActivateCount);
        Assert.AreEqual(1, target.FocusCount);
        Assert.IsTrue(target.AllCallsWereDispatched);
    }

    [TestMethod]
    public async Task VisibleNormalWindowIsActivatedWithoutRestoreOrShow()
    {
        var dispatcher = new RecordingWindowsUiDispatcher();
        var target = new FakeWindowsWindowActivationTarget(() => dispatcher.IsInvoking)
        {
            IsMinimized = false,
            IsVisible = true
        };
        var bridge = new AvaloniaWindowActivationBridge(
            dispatcher,
            new FakeWindowsWindowActivationTargetProvider { Target = target });

        var activated = await bridge.ActivateAsync();

        Assert.IsTrue(activated);
        Assert.AreEqual(0, target.RestoreCount);
        Assert.AreEqual(0, target.ShowCount);
        Assert.AreEqual(1, target.ActivateCount);
        Assert.AreEqual(1, target.FocusCount);
    }

    [TestMethod]
    public async Task MissingMainWindowRejectsActivationSafely()
    {
        var dispatcher = new RecordingWindowsUiDispatcher();
        var bridge = new AvaloniaWindowActivationBridge(
            dispatcher,
            new FakeWindowsWindowActivationTargetProvider());

        var activated = await bridge.ActivateAsync();

        Assert.IsFalse(activated);
        Assert.AreEqual(1, dispatcher.InvokeCount);
    }
}
