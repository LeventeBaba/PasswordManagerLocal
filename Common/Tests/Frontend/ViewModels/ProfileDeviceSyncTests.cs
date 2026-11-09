using System.Reflection;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using PasswordManagerLocal.Common.Contracts.Endpoints;
using PasswordManagerLocal.Common.Contracts.Responses;
using PasswordManagerLocal.Common.Frontend.ViewModels.Pages;
using PasswordManagerLocal.Common.Tests.Fakes;

namespace PasswordManagerLocal.Common.Tests.Frontend.ViewModels;

[TestClass]
public sealed class ProfileDeviceSyncTests
{
    [TestMethod]
    [DataRow(true)]
    [DataRow(false)]
    public async Task LocalChangeAndCancelLeaveTheConfirmedStateUntouched(bool initial)
    {
        await using var preferences = await TestUiPreferencesFactory.CreateAsync();
        var (endpoints, fake) = CreateEndpoints();
        fake.InitialState = initial;
        using var model = CreateModel(preferences, endpoints);
        model.SetSessionToken(Guid.NewGuid());
        Assert.IsTrue(await model.EnsureDevicesLoadedAsync());
        var device = model.Devices.Single(item => item.IsCurrentDevice);
        await ToggleAsync(model, device);
        Assert.IsTrue(model.IsLocalSyncDialogOpen);
        Assert.AreEqual(initial, device.IsSyncOn);
        Assert.AreEqual(!initial, model.PendingLocalSyncEnabled);
        Assert.IsFalse(model.CanToggleDeviceSync);
        Assert.IsTrue(model.CanConfirmLocalSyncToggle);
        Assert.AreEqual(0, fake.Writes);
        Assert.IsTrue(model.TryNavigateBack());
        Assert.IsFalse(model.IsLocalSyncDialogOpen);
        Assert.AreEqual(initial, device.IsSyncOn);
        Assert.IsTrue(model.CanToggleDeviceSync);
        await ConfirmAsync(model);
        Assert.AreEqual(0, fake.Writes);
    }

    [TestMethod]
    [DataRow(true, true)]
    [DataRow(true, false)]
    [DataRow(false, true)]
    [DataRow(false, false)]
    public async Task BothDeviceKindsKeepOldStateUntilTheSaveCompletes(bool local, bool initial)
    {
        await using var preferences = await TestUiPreferencesFactory.CreateAsync();
        var (endpoints, fake) = CreateEndpoints();
        fake.InitialState = initial;
        fake.WriteRelease = new(TaskCreationOptions.RunContinuationsAsynchronously);
        using var model = CreateModel(preferences, endpoints);
        var token = Guid.NewGuid();
        model.SetSessionToken(token);
        Assert.IsTrue(await model.EnsureDevicesLoadedAsync());
        var device = model.Devices.Single(item => item.IsCurrentDevice == local);
        var change = ToggleAsync(model, device);
        if (local) { await change; change = ConfirmAsync(model); }
        await fake.WriteEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.IsTrue(model.IsChangingDeviceSync);
        Assert.IsFalse(model.CanToggleDeviceSync);
        Assert.IsFalse(model.CanConfirmLocalSyncToggle);
        Assert.IsFalse(model.CanAutomaticallyRefreshDevices);
        Assert.AreEqual(initial, device.IsSyncOn);
        await ToggleAsync(model, device);
        await ConfirmAsync(model);
        if (local)
        {
            Assert.IsTrue(model.TryNavigateBack());
            Assert.IsTrue(model.IsLocalSyncDialogOpen, "Back must not pretend to cancel a write already in progress.");
        }
        Assert.AreEqual(1, fake.Writes);
        fake.WriteRelease.TrySetResult();
        await change;
        Assert.IsFalse(model.IsChangingDeviceSync);
        Assert.IsFalse(model.IsLocalSyncDialogOpen);
        Assert.AreEqual(!initial, device.IsSyncOn);
        Assert.AreEqual(!initial, model.Devices.Single(item => item.IsCurrentDevice == local).IsSyncOn);
        Assert.AreEqual(token, fake.LastWriteToken);
        Assert.AreEqual(local ? 1 : 0, fake.LocalWrites);
        Assert.AreEqual(local ? 0 : 1, fake.RemoteWrites);
        Assert.IsTrue(model.CanToggleDeviceSync);
    }

    [TestMethod]
    [DataRow(true, false)]
    [DataRow(true, true)]
    [DataRow(false, false)]
    [DataRow(false, true)]
    public async Task FailedSaveReadsBackWhetherTheMutationWasActuallyCommitted(bool local, bool committed)
    {
        await using var preferences = await TestUiPreferencesFactory.CreateAsync();
        var (endpoints, fake) = CreateEndpoints();
        fake.FailWrite = true;
        fake.CommitBeforeFailure = committed;
        using var model = CreateModel(preferences, endpoints);
        model.SetSessionToken(Guid.NewGuid());
        Assert.IsTrue(await model.EnsureDevicesLoadedAsync());
        var device = model.Devices.Single(item => item.IsCurrentDevice == local);
        await ToggleAsync(model, device);
        if (local) await ConfirmAsync(model);
        Assert.AreEqual(!committed, device.IsSyncOn);
        Assert.AreEqual(!committed, model.Devices.Single(item => item.IsCurrentDevice == local).IsSyncOn);
        Assert.AreEqual(local && !committed, model.IsLocalSyncDialogOpen);
        Assert.IsTrue(model.IsStatusMessageError);
        Assert.IsFalse(model.IsChangingDeviceSync);
        Assert.AreEqual(1, fake.Writes);
        Assert.IsTrue(fake.LocalReads > 0 || !local);
    }

    [TestMethod]
    public async Task FailedReadBackKeepsLastKnownStateAndLaterRefreshReconcilesIt()
    {
        await using var preferences = await TestUiPreferencesFactory.CreateAsync();
        var (endpoints, fake) = CreateEndpoints();
        fake.FailWrite = true;
        fake.CommitBeforeFailure = true;
        fake.FailLocalRead = true;
        using var model = CreateModel(preferences, endpoints);
        model.SetSessionToken(Guid.NewGuid());
        Assert.IsTrue(await model.EnsureDevicesLoadedAsync());
        await ToggleAsync(model, model.Devices.Single(item => item.IsCurrentDevice));
        await ConfirmAsync(model);
        Assert.IsTrue(model.Devices.Single(item => item.IsCurrentDevice).IsSyncOn);
        Assert.IsTrue(model.IsStatusMessageError);
        Assert.IsTrue(model.TryNavigateBack());
        Assert.IsTrue(await model.RefreshLoadedDevicesAsync());
        Assert.IsFalse(model.Devices.Single(item => item.IsCurrentDevice).IsSyncOn);
    }

    [TestMethod]
    public async Task AFailedReloadAfterSuccessfulSaveRetainsTheConfirmedChange()
    {
        await using var preferences = await TestUiPreferencesFactory.CreateAsync();
        var (endpoints, fake) = CreateEndpoints();
        fake.FailListAfterWrite = true;
        using var model = CreateModel(preferences, endpoints);
        model.SetSessionToken(Guid.NewGuid());
        Assert.IsTrue(await model.EnsureDevicesLoadedAsync());
        await ToggleAsync(model, model.Devices.Single(item => item.IsCurrentDevice));
        await ConfirmAsync(model);
        Assert.IsFalse(model.Devices.Single(item => item.IsCurrentDevice).IsSyncOn);
        Assert.IsFalse(model.IsLocalSyncDialogOpen);
        Assert.IsTrue(model.IsStatusMessageError);
        Assert.IsFalse(model.IsChangingDeviceSync);
    }

    [TestMethod]
    public async Task SessionChangeInvalidatesAnUnconfirmedRequestAndItsRows()
    {
        await using var preferences = await TestUiPreferencesFactory.CreateAsync();
        var (endpoints, fake) = CreateEndpoints();
        using var model = CreateModel(preferences, endpoints);
        model.SetSessionToken(Guid.NewGuid());
        Assert.IsTrue(await model.EnsureDevicesLoadedAsync());
        var previous = model.Devices.Single(item => item.IsCurrentDevice);
        await ToggleAsync(model, previous);
        model.SetSessionToken(Guid.NewGuid());
        Assert.IsFalse(model.AreDevicesLoaded);
        Assert.AreEqual(0, model.Devices.Count);
        Assert.IsFalse(model.IsLocalSyncDialogOpen);
        await ConfirmAsync(model);
        await ToggleAsync(model, previous);
        Assert.AreEqual(0, fake.Writes);
    }

    [TestMethod]
    [DataRow(true)]
    [DataRow(false)]
    public async Task AnOldSaveCannotChangeTheNextProfileOrCloseItsConfirmation(bool local)
    {
        await using var preferences = await TestUiPreferencesFactory.CreateAsync();
        var (endpoints, fake) = CreateEndpoints();
        fake.WriteRelease = new(TaskCreationOptions.RunContinuationsAsynchronously);
        using var model = CreateModel(preferences, endpoints);
        var oldToken = Guid.NewGuid();
        model.SetSessionToken(oldToken);
        Assert.IsTrue(await model.EnsureDevicesLoadedAsync());
        var oldChange = ToggleAsync(model, model.Devices.Single(item => item.IsCurrentDevice == local));
        if (local) { await oldChange; oldChange = ConfirmAsync(model); }
        await fake.WriteEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        model.SetSessionToken(Guid.NewGuid());
        Assert.IsTrue(await model.EnsureDevicesLoadedAsync());
        var nextDevice = model.Devices.Single(item => item.IsCurrentDevice);
        await ToggleAsync(model, nextDevice);
        fake.WriteRelease.TrySetResult();
        await oldChange;
        Assert.IsTrue(model.IsLocalSyncDialogOpen);
        Assert.AreSame(nextDevice, model.PendingLocalSyncDevice);
        Assert.IsTrue(nextDevice.IsSyncOn);
        Assert.IsFalse(model.IsChangingDeviceSync);
        Assert.AreEqual(oldToken, fake.LastWriteToken);
        Assert.AreEqual(1, fake.Writes);
    }

    [TestMethod]
    public async Task ReturningToDevicesWhileSavingDoesNotStartAnOverlappingWrite()
    {
        await using var preferences = await TestUiPreferencesFactory.CreateAsync();
        var (endpoints, fake) = CreateEndpoints();
        fake.WriteRelease = new(TaskCreationOptions.RunContinuationsAsynchronously);
        using var model = CreateModel(preferences, endpoints);
        model.SetSessionToken(Guid.NewGuid());
        Assert.IsTrue(await model.EnsureDevicesLoadedAsync());
        var change = ToggleAsync(model, model.Devices.Single(item => !item.IsCurrentDevice));
        await fake.WriteEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        model.ShowProfileMainPage();
        model.ShowDevicesMainPage();
        Assert.IsTrue(model.IsChangingDeviceSync);
        Assert.IsFalse(model.CanToggleDeviceSync);
        await ToggleAsync(model, model.Devices.Single(item => !item.IsCurrentDevice));
        Assert.AreEqual(1, fake.Writes);
        fake.WriteRelease.TrySetResult();
        await change;
        Assert.IsFalse(model.IsChangingDeviceSync);
        Assert.IsFalse(model.Devices.Single(item => !item.IsCurrentDevice).IsSyncOn);
    }

    [TestMethod]
    public async Task AnOldCompletionCannotReenableSwitchesWhileTheNextProfileIsSaving()
    {
        await using var preferences = await TestUiPreferencesFactory.CreateAsync();
        var (endpoints, fake) = CreateEndpoints();
        var oldRelease = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var newRelease = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        fake.WriteRelease = oldRelease;
        using var model = CreateModel(preferences, endpoints);
        model.SetSessionToken(Guid.NewGuid());
        Assert.IsTrue(await model.EnsureDevicesLoadedAsync());
        var oldChange = ToggleAsync(model, model.Devices.Single(item => !item.IsCurrentDevice));
        await fake.WriteEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        model.SetSessionToken(Guid.NewGuid());
        Assert.IsTrue(await model.EnsureDevicesLoadedAsync());
        fake.WriteRelease = newRelease;
        var newChange = ToggleAsync(model, model.Devices.Single(item => !item.IsCurrentDevice));
        Assert.AreEqual(2, fake.Writes);
        oldRelease.TrySetResult();
        await oldChange;
        Assert.IsTrue(model.IsChangingDeviceSync);
        Assert.IsFalse(model.CanToggleDeviceSync);
        Assert.IsTrue(model.Devices.Single(item => !item.IsCurrentDevice).IsSyncOn);
        newRelease.TrySetResult();
        await newChange;
        Assert.IsFalse(model.IsChangingDeviceSync);
        Assert.IsFalse(model.Devices.Single(item => !item.IsCurrentDevice).IsSyncOn);
    }

    [TestMethod]
    public async Task LocalRefreshReadsTheSignedInProfileInsteadOfGlobalDeviceState()
    {
        await using var preferences = await TestUiPreferencesFactory.CreateAsync();
        var (endpoints, fake) = CreateEndpoints();
        using var model = CreateModel(preferences, endpoints);
        var token = Guid.NewGuid();
        model.SetSessionToken(token);
        Assert.IsTrue(await model.EnsureDevicesLoadedAsync());
        model.ShowDevicesMainPage();
        fake.Local[token] = false;
        Assert.IsTrue(await model.RefreshLocalUserSyncAutomaticallyAsync(() => true, CancellationToken.None));
        Assert.IsFalse(model.Devices.Single(item => item.IsCurrentDevice).IsSyncOn);
        Assert.IsTrue(model.Devices.Single(item => !item.IsCurrentDevice).IsSyncOn);
        Assert.AreEqual(1, fake.LocalReads);
        Assert.AreEqual(0, fake.GlobalReads);
    }

    [TestMethod]
    [DataRow(true)]
    [DataRow(false)]
    public async Task ARefreshStartedBeforeAChangeCannotOverwriteTheNewState(bool localRefresh)
    {
        await using var preferences = await TestUiPreferencesFactory.CreateAsync();
        var (endpoints, fake) = CreateEndpoints();
        using var model = CreateModel(preferences, endpoints);
        model.SetSessionToken(Guid.NewGuid());
        Assert.IsTrue(await model.EnsureDevicesLoadedAsync());
        model.ShowDevicesMainPage();
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        if (localRefresh) fake.NextLocalReadRelease = release.Task;
        else fake.NextListReadRelease = release.Task;
        var refresh = localRefresh
            ? model.RefreshLocalUserSyncAutomaticallyAsync(() => true, CancellationToken.None)
            : model.RefreshDevicesAutomaticallyAsync(() => true, CancellationToken.None);
        var device = model.Devices.Single(item => item.IsCurrentDevice == localRefresh);
        await ToggleAsync(model, device);
        if (localRefresh) await ConfirmAsync(model);
        release.TrySetResult();
        Assert.IsFalse(await refresh);
        Assert.IsFalse(model.Devices.Single(item => item.IsCurrentDevice == localRefresh).IsSyncOn);
    }

    [TestMethod]
    public async Task AReplacedRowCannotStartASecondRequestUsingAStaleSnapshot()
    {
        await using var preferences = await TestUiPreferencesFactory.CreateAsync();
        var (endpoints, fake) = CreateEndpoints();
        using var model = CreateModel(preferences, endpoints);
        model.SetSessionToken(Guid.NewGuid());
        Assert.IsTrue(await model.EnsureDevicesLoadedAsync());
        var previous = model.Devices.Single(item => item.IsCurrentDevice);
        Assert.IsTrue(await model.RefreshLoadedDevicesAsync());
        await ToggleAsync(model, previous);
        Assert.IsFalse(model.IsLocalSyncDialogOpen);
        Assert.AreEqual(0, fake.Writes);
    }

    private static ProfileViewModel CreateModel(
        PasswordManagerLocal.Common.Frontend.Services.UiPreferencesService preferences, IEndpoints endpoints) =>
        new(preferences, endpoints, () => Task.FromResult(true), () => Task.CompletedTask);

    private static (IEndpoints Endpoints, SyncEndpoints Fake) CreateEndpoints()
    {
        var endpoints = DispatchProxy.Create<IEndpoints, SyncEndpoints>();
        return (endpoints, (SyncEndpoints)(object)endpoints);
    }

    private static Task ToggleAsync(ProfileViewModel model, DeviceItemViewModel device) => InvokeAsync(model, "ToggleDeviceSyncAsync", device);
    private static Task ConfirmAsync(ProfileViewModel model) => InvokeAsync(model, "ConfirmLocalSyncToggleAsync");
    private static Task InvokeAsync(ProfileViewModel model, string name, params object[] args) =>
        (Task)typeof(ProfileViewModel).GetMethod(name, BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(model, args)!;

    public class SyncEndpoints : DispatchProxy
    {
        private readonly Guid _localId = Guid.NewGuid();
        private readonly Guid _remoteId = Guid.NewGuid();
        public Dictionary<Guid, bool> Local { get; } = [];
        private Dictionary<Guid, bool> Remote { get; } = [];
        public bool InitialState { get; set; } = true;
        public bool FailWrite { get; set; }
        public bool CommitBeforeFailure { get; set; }
        public bool FailLocalRead { get; set; }
        public bool FailListAfterWrite { get; set; }
        public int Writes => LocalWrites + RemoteWrites;
        public int LocalWrites { get; private set; }
        public int RemoteWrites { get; private set; }
        public int LocalReads { get; private set; }
        public int GlobalReads { get; private set; }
        public Guid LastWriteToken { get; private set; }
        public TaskCompletionSource WriteEntered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource? WriteRelease { get; set; }
        public Task? NextLocalReadRelease { get; set; }
        public Task? NextListReadRelease { get; set; }

        protected override object? Invoke(MethodInfo? method, object?[]? args)
        {
            var token = args?.FirstOrDefault() is Guid value ? value : Guid.Empty;
            switch (method?.Name)
            {
                case nameof(IEndpoints.GetUserDevicesAsync):
                    var snapshot = new UserDeviceInfoResponse[]
                    {
                        new() { DeviceId = _localId, IsCurrentDevice = true, IsSyncOn = State(Local, token), Name = "This device" },
                        new() { DeviceId = _remoteId, IsSyncOn = State(Remote, token), Name = "Other device" }
                    };
                    var listRelease = NextListReadRelease;
                    NextListReadRelease = null;
                    return ReadListAsync(snapshot, listRelease);
                case nameof(IEndpoints.GetLocalUserSyncOnAsync):
                    LocalReads++;
                    var localRelease = NextLocalReadRelease;
                    NextLocalReadRelease = null;
                    return ReadLocalAsync(State(Local, token), localRelease);
                case nameof(IEndpoints.GetLocalDeviceInfoAsync):
                    GlobalReads++;
                    return Task.FromResult(new LocalDeviceInfoResponse { DeviceId = _localId, IsSyncOn = true });
                case nameof(IEndpoints.SetLocalUserSyncOnAsync):
                    LocalWrites++;
                    return SaveAsync(Local, token, (bool)args![1]!);
                case nameof(IEndpoints.SetUserDeviceSyncOnAsync):
                    RemoteWrites++;
                    Assert.AreEqual(_remoteId, (Guid)args![1]!);
                    return SaveAsync(Remote, token, (bool)args[2]!);
                default: throw new NotSupportedException(method?.Name);
            }
        }

        private bool State(Dictionary<Guid, bool> settings, Guid token) => settings.GetValueOrDefault(token, InitialState);
        private async Task SaveAsync(Dictionary<Guid, bool> settings, Guid token, bool enabled)
        {
            LastWriteToken = token;
            WriteEntered.TrySetResult();
            if (WriteRelease is not null) await WriteRelease.Task;
            if (!FailWrite || CommitBeforeFailure) settings[token] = enabled;
            if (FailWrite) throw new InvalidOperationException("Simulated synchronization save failure.");
        }
        private async Task<bool> ReadLocalAsync(bool value, Task? release)
        {
            if (release is not null) await release;
            if (FailLocalRead) throw new InvalidOperationException("Simulated read-back failure.");
            return value;
        }
        private async Task<IReadOnlyList<UserDeviceInfoResponse>> ReadListAsync(UserDeviceInfoResponse[] snapshot, Task? release)
        {
            if (release is not null) await release;
            if (FailListAfterWrite && Writes > 0) throw new InvalidOperationException("Simulated list reload failure.");
            return snapshot;
        }
    }
}
