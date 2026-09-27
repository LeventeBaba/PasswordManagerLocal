using Microsoft.VisualStudio.TestTools.UnitTesting;
using PasswordManagerLocal.Windows.Agent.Background;
using PasswordManagerLocal.Windows.Ipc.Client;
using PasswordManagerLocal.Windows.Ipc.Contracts;
using PasswordManagerLocal.Windows.Ipc.Server;
using PasswordManagerLocal.Windows.Tests.IPC.Infrastructure;

namespace PasswordManagerLocal.Windows.Tests.IPC.Background;

[TestClass]
public sealed class WindowsBackgroundSyncControlOperationTests
{
    [TestMethod]
    public async Task GetBackgroundStateReturnsAuthoritativeCoordinatorSnapshot()
    {
        var coordinator = new FakeWindowsBackgroundSyncCoordinator
        {
            State = FakeWindowsBackgroundSyncCoordinator.OperationalState()
        };
        await using var session = await IpcTestSession.CreateAsync(
            new IWindowsIpcRequestHandler[]
            {
                new GetBackgroundSyncStateWindowsIpcRequestHandler(coordinator)
            });
        var client = new WindowsIpcControlClient(session.Client, session.Serializer);

        var state = await client.GetBackgroundSyncStateAsync();

        Assert.AreEqual(coordinator.State, state);
        Assert.AreEqual(1, coordinator.ReadCount);
    }

    [TestMethod]
    public async Task SetBackgroundStateTransmitsDesiredBooleanAndReturnsFinalSnapshot()
    {
        var coordinator = new FakeWindowsBackgroundSyncCoordinator();
        await using var session = await IpcTestSession.CreateAsync(
            new IWindowsIpcRequestHandler[]
            {
                new SetBackgroundSyncEnabledWindowsIpcRequestHandler(coordinator)
            });
        var client = new WindowsIpcControlClient(session.Client, session.Serializer);

        var state = await client.SetBackgroundSyncEnabledAsync(
            new SetBackgroundSyncEnabledRequestDto(true));

        Assert.AreEqual(true, coordinator.LastRequestedEnabled);
        Assert.AreEqual(WindowsBackgroundSyncConsistency.Operational, state.Consistency);
    }
    [TestMethod]
    public async Task SetBackgroundStateUpdatesTrayVisibilityFromFinalAuthoritativeState()
    {
        var coordinator = new FakeWindowsBackgroundSyncCoordinator();
        var tray = new FakeTrayIconController();
        await using var session = await IpcTestSession.CreateAsync(
            new IWindowsIpcRequestHandler[]
            {
                new SetBackgroundSyncEnabledWindowsIpcRequestHandler(
                    coordinator,
                    trayIcon: tray)
            });
        var client = new WindowsIpcControlClient(session.Client, session.Serializer);

        await client.SetBackgroundSyncEnabledAsync(
            new SetBackgroundSyncEnabledRequestDto(true));

        Assert.AreEqual(1, tray.SetVisibleCount);
        Assert.AreEqual(true, tray.LastVisible);
    }

}
