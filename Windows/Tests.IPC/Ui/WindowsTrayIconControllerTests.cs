using Microsoft.VisualStudio.TestTools.UnitTesting;
using PasswordManagerLocal.Windows.Agent.Tray;
using PasswordManagerLocal.Windows.Tests.IPC.Infrastructure;

namespace PasswordManagerLocal.Windows.Tests.IPC.Ui;

[TestClass]
public sealed class WindowsTrayIconControllerTests
{
    [TestMethod]
    public async Task LeftClickTriggersOpenOnceAndRightClickDoesNot()
    {
        var adapter = new FakeTrayIconAdapter();
        await using var controller = new WindowsTrayIconController(adapter);
        var openCount = 0;
        controller.OpenRequested += (_, _) => openCount++;
        await controller.InitializeAsync();

        adapter.RaiseMouse(TrayIconMouseButton.Left);
        adapter.RaiseMouse(TrayIconMouseButton.Right);
        adapter.RaiseMouse(TrayIconMouseButton.Left, clicks: 2);

        Assert.AreEqual(1, openCount);
    }

    [TestMethod]
    public async Task ContextMenuCommandsRaiseOpenAndExit()
    {
        var adapter = new FakeTrayIconAdapter();
        await using var controller = new WindowsTrayIconController(adapter);
        var openCount = 0;
        var exitCount = 0;
        controller.OpenRequested += (_, _) => openCount++;
        controller.ExitRequested += (_, _) => exitCount++;
        await controller.InitializeAsync();

        adapter.RaiseOpenCommand();
        adapter.RaiseExitCommand();

        Assert.AreEqual(1, openCount);
        Assert.AreEqual(1, exitCount);
    }



    [TestMethod]
    public async Task ContextMenuOpeningAndTextUpdatesAreForwarded()
    {
        var adapter = new FakeTrayIconAdapter();
        await using var controller = new WindowsTrayIconController(adapter);
        var openingCount = 0;
        controller.ContextMenuOpening += (_, _) => openingCount++;
        await controller.InitializeAsync();
        var text = new WindowsAgentTrayText(
            "tip", "open", "exit", "error", "start", "start-message", "stop", "stop-message");

        adapter.RaiseContextMenuOpening();
        await controller.UpdateTextAsync(text);

        Assert.AreEqual(1, openingCount);
        Assert.AreSame(text, adapter.LastText);
        Assert.AreEqual(1, adapter.UpdateTextCount);
    }

    [TestMethod]
    public async Task ExitFailureIsSurfacedWithoutDisposingTray()
    {
        var adapter = new FakeTrayIconAdapter();
        await using var controller = new WindowsTrayIconController(adapter);
        await controller.InitializeAsync();

        await controller.ShowExitFailureAsync("The UI did not acknowledge shutdown.");

        Assert.AreEqual(1, adapter.ShowErrorCount);
        Assert.AreEqual("The UI did not acknowledge shutdown.", adapter.LastErrorMessage);
        Assert.AreEqual(0, adapter.HideAndDisposeCount);
    }


    [TestMethod]
    public async Task DuplicateVisibilityRequestsAreCoalesced()
    {
        var adapter = new FakeTrayIconAdapter();
        await using var controller = new WindowsTrayIconController(adapter);
        await controller.InitializeAsync();

        await controller.SetVisibleAsync(true);
        await controller.SetVisibleAsync(true);
        await controller.SetVisibleAsync(false);
        await controller.SetVisibleAsync(false);

        Assert.AreEqual(2, adapter.SetVisibleCount);
        Assert.AreEqual(false, adapter.LastVisible);
    }

    [TestMethod]
    public async Task VisibilityRequestsAreRejectedAfterDisposal()
    {
        var adapter = new FakeTrayIconAdapter();
        var controller = new WindowsTrayIconController(adapter);
        await controller.InitializeAsync();
        await controller.DisposeAsync();

        await Assert.ThrowsExactlyAsync<ObjectDisposedException>(
            async () => await controller.SetVisibleAsync(true));
    }

    [TestMethod]
    public async Task DisposalHidesAndDisposesIconAndIsIdempotent()
    {
        var adapter = new FakeTrayIconAdapter();
        var controller = new WindowsTrayIconController(adapter);
        await controller.InitializeAsync();

        await controller.DisposeAsync();
        await controller.DisposeAsync();

        Assert.AreEqual(1, adapter.HideAndDisposeCount);
    }
}
