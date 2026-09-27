using Microsoft.VisualStudio.TestTools.UnitTesting;
using PasswordManagerLocal.Common.Backend.Hosting;
using PasswordManagerLocal.Common.Contracts.Runtime;
using PasswordManagerLocal.Common.Contracts.BackgroundSync;
using PasswordManagerLocal.Windows.Agent.Background;
using PasswordManagerLocal.Windows.Agent.Hosting;
using PasswordManagerLocal.Windows.Agent.Lifecycle;
using PasswordManagerLocal.Windows.Ipc.Contracts;
using PasswordManagerLocal.Windows.Tests.IPC.Infrastructure;

namespace PasswordManagerLocal.Windows.Tests.IPC.Background;

[TestClass]
public sealed class WindowsBackgroundSyncCoordinatorTests
{
    [TestMethod]
    public async Task StartupWithEnabledSettingRepairsRegistrationAndAcquiresOneLease()
    {
        var store = new FakeBackgroundSyncSettingsStore
        {
            Settings = new BackgroundSyncSettings(true)
        };
        var startup = new FakeWindowsStartupRegistration();
        var owner = new FakeWindowsAgentBackendRuntimeOwner();
        using var transitions = new WindowsAgentLifecycleTransitionCoordinator();
        var coordinator = CreateCoordinator(store, startup, owner, transitions);

        await coordinator.InitializeAsync();
        var state = await coordinator.GetStateAsync();

        Assert.AreEqual(1, startup.RegisterCount);
        Assert.AreEqual(1, owner.BackgroundLeaseAcquireCount);
        Assert.IsTrue(state.IsEnabled);
        Assert.IsTrue(state.IsStartupRegistered);
        Assert.IsTrue(state.IsBackgroundLeaseActive);
        Assert.AreEqual(WindowsBackgroundSyncConsistency.Operational, state.Consistency);
    }

    [TestMethod]
    public async Task BackgroundLaunchWithDisabledSettingDoesNotEnableOrAcquireLease()
    {
        var store = new FakeBackgroundSyncSettingsStore();
        var startup = new FakeWindowsStartupRegistration
        {
            Snapshot = new WindowsStartupRegistrationSnapshot(true, false, "stale")
        };
        var owner = new FakeWindowsAgentBackendRuntimeOwner();
        using var transitions = new WindowsAgentLifecycleTransitionCoordinator();
        var coordinator = CreateCoordinator(store, startup, owner, transitions);

        await coordinator.InitializeAsync();
        var state = await coordinator.GetStateAsync();

        Assert.IsFalse(store.Settings.IsEnabled);
        Assert.AreEqual(0, store.WriteCount);
        Assert.AreEqual(0, owner.BackgroundLeaseAcquireCount);
        Assert.AreEqual(1, startup.UnregisterCount);
        Assert.AreEqual(WindowsBackgroundSyncConsistency.Disabled, state.Consistency);
    }


    [TestMethod]
    public async Task DisabledStartupRegistryCleanupFailureIsClassifiedAsStartupRegistration()
    {
        var store = new FakeBackgroundSyncSettingsStore();
        var startup = new FakeWindowsStartupRegistration
        {
            Snapshot = new WindowsStartupRegistrationSnapshot(true, false, "stale"),
            UnregisterFailure = new UnauthorizedAccessException("denied")
        };
        var owner = new FakeWindowsAgentBackendRuntimeOwner();
        using var transitions = new WindowsAgentLifecycleTransitionCoordinator();
        var coordinator = CreateCoordinator(store, startup, owner, transitions);

        await coordinator.InitializeAsync();
        var state = await coordinator.GetStateAsync();

        Assert.AreEqual(
            WindowsBackgroundSyncFailureKind.StartupRegistration,
            state.FailureKind);
        Assert.AreEqual(0, owner.BackgroundLeaseAcquireCount);
        Assert.AreNotEqual(WindowsBackgroundSyncConsistency.Operational, state.Consistency);
    }

    [TestMethod]
    public async Task DisabledStartupLeaseCleanupFailureIsClassifiedAsRuntimeLease()
    {
        var store = new FakeBackgroundSyncSettingsStore();
        var startup = new FakeWindowsStartupRegistration();
        var owner = new FakeWindowsAgentBackendRuntimeOwner();
        using var transitions = new WindowsAgentLifecycleTransitionCoordinator();
        var coordinator = CreateOperationalCoordinator(store, startup, owner, transitions);
        await coordinator.SetEnabledAsync(true);
        store.Settings = new BackgroundSyncSettings(false);
        owner.BackgroundLeaseDisposeFailure = new IOException("release failed");

        await coordinator.InitializeAsync();
        var state = await coordinator.GetStateAsync();

        Assert.AreEqual(WindowsBackgroundSyncFailureKind.RuntimeLease, state.FailureKind);
        Assert.AreNotEqual(WindowsBackgroundSyncConsistency.Operational, state.Consistency);
    }

    [TestMethod]
    public async Task DisabledStartupCleanupSuccessReportsDisabled()
    {
        var store = new FakeBackgroundSyncSettingsStore();
        var startup = new FakeWindowsStartupRegistration
        {
            Snapshot = new WindowsStartupRegistrationSnapshot(true, false, "stale")
        };
        var owner = new FakeWindowsAgentBackendRuntimeOwner();
        using var transitions = new WindowsAgentLifecycleTransitionCoordinator();
        var coordinator = CreateCoordinator(store, startup, owner, transitions);

        await coordinator.InitializeAsync();
        var state = await coordinator.GetStateAsync();

        Assert.AreEqual(WindowsBackgroundSyncConsistency.Disabled, state.Consistency);
        Assert.AreEqual(WindowsBackgroundSyncFailureKind.None, state.FailureKind);
    }

    [TestMethod]
    public async Task SuccessfulEnableCommitsSettingRegistrationLeaseAndRuntime()
    {
        var store = new FakeBackgroundSyncSettingsStore();
        var startup = new FakeWindowsStartupRegistration();
        var owner = new FakeWindowsAgentBackendRuntimeOwner();
        using var transitions = new WindowsAgentLifecycleTransitionCoordinator();
        var coordinator = CreateOperationalCoordinator(store, startup, owner, transitions);

        var state = await coordinator.SetEnabledAsync(true);

        Assert.IsTrue(store.Settings.IsEnabled);
        Assert.IsTrue(startup.Snapshot.IsRegistered);
        Assert.AreEqual(1, owner.BackgroundLeaseAcquireCount);
        Assert.IsTrue(state.IsRuntimeRunning);
        Assert.AreEqual(WindowsBackgroundSyncConsistency.Operational, state.Consistency);
    }

    [TestMethod]
    public async Task RepeatedEnableDoesNotAcquireDuplicateLease()
    {
        var store = new FakeBackgroundSyncSettingsStore();
        var startup = new FakeWindowsStartupRegistration();
        var owner = new FakeWindowsAgentBackendRuntimeOwner();
        using var transitions = new WindowsAgentLifecycleTransitionCoordinator();
        var coordinator = CreateOperationalCoordinator(store, startup, owner, transitions);

        await coordinator.SetEnabledAsync(true);
        await coordinator.SetEnabledAsync(true);

        Assert.AreEqual(1, owner.BackgroundLeaseAcquireCount);
        Assert.AreEqual(0, owner.LastBackgroundLease?.DisposeCount);
    }

    [TestMethod]
    public async Task DisableReleasesBackgroundLeaseExactlyOnceAndStopsBackgroundOnlyRuntime()
    {
        var store = new FakeBackgroundSyncSettingsStore();
        var startup = new FakeWindowsStartupRegistration();
        var owner = new FakeWindowsAgentBackendRuntimeOwner();
        using var transitions = new WindowsAgentLifecycleTransitionCoordinator();
        var coordinator = CreateOperationalCoordinator(store, startup, owner, transitions);
        await coordinator.SetEnabledAsync(true);
        var lease = owner.LastBackgroundLease;

        var state = await coordinator.SetEnabledAsync(false);
        var repeated = await coordinator.SetEnabledAsync(false);

        Assert.IsNotNull(lease);
        Assert.AreEqual(1, lease.DisposeCount);
        Assert.AreEqual(WindowsBackgroundSyncConsistency.Disabled, state.Consistency);
        Assert.AreEqual(WindowsBackgroundSyncConsistency.Disabled, repeated.Consistency);
        Assert.AreEqual(BackendRuntimeState.Stopped, owner.Snapshot.Runtime.State);
    }

    [TestMethod]
    public async Task DisableKeepsRuntimeRunningWhileInteractiveUiLeaseRemains()
    {
        var store = new FakeBackgroundSyncSettingsStore();
        var startup = new FakeWindowsStartupRegistration();
        var owner = new FakeWindowsAgentBackendRuntimeOwner
        {
            Snapshot = FakeWindowsAgentBackendRuntimeOwner.CreateSnapshot(
                runtimeState: BackendRuntimeState.Ready,
                activeReasons: BackendLifetimeReason.InteractiveUi)
        };
        using var transitions = new WindowsAgentLifecycleTransitionCoordinator();
        var coordinator = CreateOperationalCoordinator(store, startup, owner, transitions);
        await coordinator.SetEnabledAsync(true);

        var state = await coordinator.SetEnabledAsync(false);

        Assert.AreEqual(WindowsBackgroundSyncConsistency.Disabled, state.Consistency);
        Assert.IsTrue(state.IsRuntimeRunning);
        Assert.AreEqual(BackendLifetimeReason.InteractiveUi, owner.Snapshot.ActiveReasons);
    }

    [TestMethod]
    public async Task StartupRegistrationFailureDoesNotEnableSettingOrAcquireLease()
    {
        var store = new FakeBackgroundSyncSettingsStore();
        var startup = new FakeWindowsStartupRegistration
        {
            RegisterFailure = new UnauthorizedAccessException("denied")
        };
        var owner = new FakeWindowsAgentBackendRuntimeOwner();
        using var transitions = new WindowsAgentLifecycleTransitionCoordinator();
        var coordinator = CreateOperationalCoordinator(store, startup, owner, transitions);

        var state = await coordinator.SetEnabledAsync(true);

        Assert.IsFalse(store.Settings.IsEnabled);
        Assert.AreEqual(0, owner.BackgroundLeaseAcquireCount);
        Assert.AreEqual(WindowsBackgroundSyncFailureKind.StartupRegistration, state.FailureKind);
        Assert.IsFalse(state.IsEnabled);
    }

    [TestMethod]
    public async Task SettingPersistenceFailureRollsBackRegistrationAndAcquiresNoLease()
    {
        var store = new FakeBackgroundSyncSettingsStore
        {
            WriteFailure = new IOException("write failed")
        };
        var startup = new FakeWindowsStartupRegistration();
        var owner = new FakeWindowsAgentBackendRuntimeOwner();
        using var transitions = new WindowsAgentLifecycleTransitionCoordinator();
        var coordinator = CreateOperationalCoordinator(store, startup, owner, transitions);

        var state = await coordinator.SetEnabledAsync(true);

        Assert.IsFalse(store.Settings.IsEnabled);
        Assert.IsFalse(startup.Snapshot.EntryExists);
        Assert.AreEqual(0, owner.BackgroundLeaseAcquireCount);
        Assert.AreEqual(WindowsBackgroundSyncFailureKind.Rollback, state.FailureKind);
    }

    [TestMethod]
    public async Task LeaseAcquisitionFailureCompensatesSettingAndRegistration()
    {
        var store = new FakeBackgroundSyncSettingsStore();
        var startup = new FakeWindowsStartupRegistration();
        var owner = new FakeWindowsAgentBackendRuntimeOwner
        {
            BackgroundLeaseFailure = new InvalidOperationException("runtime failed")
        };
        using var transitions = new WindowsAgentLifecycleTransitionCoordinator();
        var coordinator = CreateOperationalCoordinator(store, startup, owner, transitions);

        var state = await coordinator.SetEnabledAsync(true);

        Assert.IsFalse(store.Settings.IsEnabled);
        Assert.IsFalse(startup.Snapshot.EntryExists);
        Assert.IsFalse(state.IsBackgroundLeaseActive);
        Assert.AreEqual(WindowsBackgroundSyncFailureKind.RuntimeLease, state.FailureKind);
    }

    [TestMethod]
    public async Task RegistryRemovalFailureRestoresPreviousEnabledState()
    {
        var store = new FakeBackgroundSyncSettingsStore();
        var startup = new FakeWindowsStartupRegistration();
        var owner = new FakeWindowsAgentBackendRuntimeOwner();
        using var transitions = new WindowsAgentLifecycleTransitionCoordinator();
        var coordinator = CreateOperationalCoordinator(store, startup, owner, transitions);
        await coordinator.SetEnabledAsync(true);
        startup.UnregisterFailure = new UnauthorizedAccessException("denied");

        var state = await coordinator.SetEnabledAsync(false);

        Assert.IsTrue(store.Settings.IsEnabled);
        Assert.IsTrue(startup.Snapshot.IsRegistered);
        Assert.IsTrue(state.IsBackgroundLeaseActive);
        Assert.AreEqual(WindowsBackgroundSyncFailureKind.StartupRegistration, state.FailureKind);
        Assert.AreEqual(WindowsBackgroundSyncConsistency.Degraded, state.Consistency);
    }

    [TestMethod]
    public async Task LeaseReleaseFailureNeverReportsFullyDisabled()
    {
        var store = new FakeBackgroundSyncSettingsStore();
        var startup = new FakeWindowsStartupRegistration();
        var owner = new FakeWindowsAgentBackendRuntimeOwner();
        using var transitions = new WindowsAgentLifecycleTransitionCoordinator();
        var coordinator = CreateOperationalCoordinator(store, startup, owner, transitions);
        await coordinator.SetEnabledAsync(true);
        owner.BackgroundLeaseDisposeFailure = new IOException("release failed");

        var state = await coordinator.SetEnabledAsync(false);

        Assert.AreNotEqual(WindowsBackgroundSyncConsistency.Disabled, state.Consistency);
        Assert.IsNotNull(state.Failure);
        Assert.IsTrue(state.IsEnabled);
    }

    [TestMethod]
    public async Task ConcurrentSettingChangesAreSerialized()
    {
        var writeEntered = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var writeRelease = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var store = new FakeBackgroundSyncSettingsStore
        {
            WriteEntered = writeEntered,
            WriteRelease = writeRelease.Task
        };
        var startup = new FakeWindowsStartupRegistration();
        var owner = new FakeWindowsAgentBackendRuntimeOwner();
        using var transitions = new WindowsAgentLifecycleTransitionCoordinator();
        var coordinator = CreateOperationalCoordinator(store, startup, owner, transitions);

        var enable = coordinator.SetEnabledAsync(true);
        await writeEntered.Task;
        var disable = coordinator.SetEnabledAsync(false);
        await Task.Delay(20);

        Assert.IsFalse(disable.IsCompleted);
        writeRelease.TrySetResult();
        await Task.WhenAll(enable, disable);
        Assert.IsFalse(store.Settings.IsEnabled);
    }

    [TestMethod]
    public async Task SettingChangeWaitsForDatabaseResetTransition()
    {
        var store = new FakeBackgroundSyncSettingsStore();
        var startup = new FakeWindowsStartupRegistration();
        var owner = new FakeWindowsAgentBackendRuntimeOwner();
        using var transitions = new WindowsAgentLifecycleTransitionCoordinator();
        var coordinator = CreateOperationalCoordinator(store, startup, owner, transitions);
        await using var reset = await transitions.EnterAsync(
            WindowsAgentLifecycleTransitionState.ResettingDatabase);

        var change = coordinator.SetEnabledAsync(true);
        await Task.Delay(20);
        Assert.IsFalse(change.IsCompleted);

        await reset.DisposeAsync();
        var state = await change;
        Assert.AreEqual(WindowsBackgroundSyncConsistency.Operational, state.Consistency);
    }

    [TestMethod]
    public async Task RestartRequiredWhileWaitingForTransitionRejectsMutationWithoutWrite()
    {
        var store = new FakeBackgroundSyncSettingsStore();
        var startup = new FakeWindowsStartupRegistration();
        var owner = new FakeWindowsAgentBackendRuntimeOwner();
        using var transitions = new WindowsAgentLifecycleTransitionCoordinator();
        var coordinator = CreateOperationalCoordinator(store, startup, owner, transitions);
        await using var reset = await transitions.EnterAsync(
            WindowsAgentLifecycleTransitionState.ResettingDatabase);
        var change = coordinator.SetEnabledAsync(true);
        owner.RequireProcessRestart(new InvalidOperationException("restart required"));

        await reset.DisposeAsync();

        await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => change);
        Assert.AreEqual(0, store.WriteCount);
    }

    [TestMethod]
    public async Task ReadDuringSettingTransitionReportsTransitioningState()
    {
        var registerEntered = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var registerRelease = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var store = new FakeBackgroundSyncSettingsStore();
        var startup = new FakeWindowsStartupRegistration
        {
            RegisterEntered = registerEntered,
            RegisterRelease = registerRelease.Task
        };
        var owner = new FakeWindowsAgentBackendRuntimeOwner();
        using var transitions = new WindowsAgentLifecycleTransitionCoordinator();
        var coordinator = CreateOperationalCoordinator(store, startup, owner, transitions);

        var change = coordinator.SetEnabledAsync(true);
        await registerEntered.Task;
        var state = await coordinator.GetStateAsync();

        Assert.IsTrue(state.IsTransitionInProgress);
        Assert.AreEqual(WindowsBackgroundSyncConsistency.Transitioning, state.Consistency);
        registerRelease.TrySetResult();
        await change;
    }

    [TestMethod]
    public async Task MutationIsRejectedWhenAgentIsFailedOrAdmissionClosed()
    {
        var store = new FakeBackgroundSyncSettingsStore();
        var startup = new FakeWindowsStartupRegistration();
        var owner = new FakeWindowsAgentBackendRuntimeOwner();
        var state = new WindowsAgentStateStore();
        state.MarkFailed("failed");
        var admission = new WindowsAgentAdmissionGate();
        using var transitions = new WindowsAgentLifecycleTransitionCoordinator();
        var coordinator = new WindowsBackgroundSyncCoordinator(
            store,
            startup,
            owner,
            state,
            admission,
            transitions,
            AgentLocalizationTestFactory.CreateEnglish());

        await Assert.ThrowsExactlyAsync<InvalidOperationException>(() =>
            coordinator.SetEnabledAsync(true));
        Assert.AreEqual(0, store.WriteCount);
        Assert.AreEqual(0, startup.RegisterCount);
    }




    [TestMethod]
    public async Task RuntimeFailureWithOwnedLeaseIsReportedAsDegradedNotActiveRuntime()
    {
        var store = new FakeBackgroundSyncSettingsStore();
        var startup = new FakeWindowsStartupRegistration();
        var owner = new FakeWindowsAgentBackendRuntimeOwner();
        using var transitions = new WindowsAgentLifecycleTransitionCoordinator();
        var coordinator = CreateOperationalCoordinator(store, startup, owner, transitions);
        await coordinator.SetEnabledAsync(true);
        owner.PublishSnapshot(owner.Snapshot with
        {
            Runtime = owner.Snapshot.Runtime with { State = BackendRuntimeState.Failed },
            State = PasswordManagerLocal.Windows.Agent.Backend.WindowsAgentBackendOwnerState.Failed
        });

        var state = await coordinator.GetStateAsync();

        Assert.IsTrue(state.IsBackgroundLeaseActive);
        Assert.IsFalse(state.IsRuntimeRunning);
        Assert.AreEqual(WindowsBackgroundSyncConsistency.Degraded, state.Consistency);
    }

    [TestMethod]
    public async Task MismatchedStartupCommandIsReportedAsStartupInconsistency()
    {
        var store = new FakeBackgroundSyncSettingsStore();
        var startup = new FakeWindowsStartupRegistration();
        var owner = new FakeWindowsAgentBackendRuntimeOwner();
        using var transitions = new WindowsAgentLifecycleTransitionCoordinator();
        var coordinator = CreateOperationalCoordinator(store, startup, owner, transitions);
        await coordinator.SetEnabledAsync(true);
        startup.Snapshot = new WindowsStartupRegistrationSnapshot(
            EntryExists: true,
            IsRegistered: false,
            Command: "different agent command");

        var state = await coordinator.GetStateAsync();

        Assert.AreEqual(WindowsBackgroundSyncConsistency.Inconsistent, state.Consistency);
        Assert.AreEqual(WindowsBackgroundSyncFailureKind.StartupRegistration, state.FailureKind);
        Assert.IsFalse(state.IsStartupRegistered);
    }

    [TestMethod]
    public async Task MalformedStartupSettingFailsSafeAndRemovesStaleStartupEntry()
    {
        var store = new FakeBackgroundSyncSettingsStore
        {
            ReadFailure = new InvalidDataException("malformed")
        };
        var startup = new FakeWindowsStartupRegistration
        {
            Snapshot = new WindowsStartupRegistrationSnapshot(true, false, "stale")
        };
        var owner = new FakeWindowsAgentBackendRuntimeOwner();
        using var transitions = new WindowsAgentLifecycleTransitionCoordinator();
        var coordinator = CreateCoordinator(store, startup, owner, transitions);

        await coordinator.InitializeAsync();
        var state = await coordinator.GetStateAsync();

        Assert.AreEqual(1, startup.UnregisterCount);
        Assert.AreEqual(0, owner.BackgroundLeaseAcquireCount);
        Assert.AreEqual(WindowsBackgroundSyncConsistency.Unavailable, state.Consistency);
        Assert.AreEqual(WindowsBackgroundSyncFailureKind.SettingRead, state.FailureKind);
    }

    [TestMethod]
    public async Task StartupRegistryFailureLeavesEnabledStateDegradedWithoutLease()
    {
        var store = new FakeBackgroundSyncSettingsStore
        {
            Settings = new BackgroundSyncSettings(true)
        };
        var startup = new FakeWindowsStartupRegistration
        {
            RegisterFailure = new UnauthorizedAccessException("denied")
        };
        var owner = new FakeWindowsAgentBackendRuntimeOwner();
        using var transitions = new WindowsAgentLifecycleTransitionCoordinator();
        var coordinator = CreateCoordinator(store, startup, owner, transitions);

        await coordinator.InitializeAsync();
        var state = await coordinator.GetStateAsync();

        Assert.IsTrue(state.IsEnabled);
        Assert.IsFalse(state.IsBackgroundLeaseActive);
        Assert.AreEqual(WindowsBackgroundSyncFailureKind.StartupRegistration, state.FailureKind);
        Assert.AreEqual(WindowsBackgroundSyncConsistency.Inconsistent, state.Consistency);
    }

    [TestMethod]
    public async Task StartupRuntimeFailureDoesNotReportBackgroundActive()
    {
        var store = new FakeBackgroundSyncSettingsStore
        {
            Settings = new BackgroundSyncSettings(true)
        };
        var startup = new FakeWindowsStartupRegistration();
        var owner = new FakeWindowsAgentBackendRuntimeOwner
        {
            BackgroundLeaseFailure = new IOException("runtime startup")
        };
        using var transitions = new WindowsAgentLifecycleTransitionCoordinator();
        var coordinator = CreateCoordinator(store, startup, owner, transitions);

        await coordinator.InitializeAsync();
        var state = await coordinator.GetStateAsync();

        Assert.IsTrue(state.IsEnabled);
        Assert.IsTrue(state.IsStartupRegistered);
        Assert.IsFalse(state.IsBackgroundLeaseActive);
        Assert.IsFalse(state.IsRuntimeRunning);
        Assert.AreEqual(WindowsBackgroundSyncConsistency.Degraded, state.Consistency);
        Assert.AreEqual(WindowsBackgroundSyncFailureKind.RuntimeLease, state.FailureKind);
    }

    [TestMethod]
    public async Task ReadDuringDatabaseResetReportsTransitioningState()
    {
        var store = new FakeBackgroundSyncSettingsStore();
        var startup = new FakeWindowsStartupRegistration();
        var owner = new FakeWindowsAgentBackendRuntimeOwner();
        using var transitions = new WindowsAgentLifecycleTransitionCoordinator();
        var coordinator = CreateOperationalCoordinator(store, startup, owner, transitions);
        await using var reset = await transitions.EnterAsync(
            WindowsAgentLifecycleTransitionState.ResettingDatabase);

        var state = await coordinator.GetStateAsync();

        Assert.IsTrue(state.IsTransitionInProgress);
        Assert.AreEqual(WindowsBackgroundSyncConsistency.Transitioning, state.Consistency);
    }

    private static WindowsBackgroundSyncCoordinator CreateOperationalCoordinator(
        FakeBackgroundSyncSettingsStore store,
        FakeWindowsStartupRegistration startup,
        FakeWindowsAgentBackendRuntimeOwner owner,
        WindowsAgentLifecycleTransitionCoordinator transitions)
    {
        var state = new WindowsAgentStateStore();
        state.MarkRunning(DateTimeOffset.UtcNow);
        var admission = new WindowsAgentAdmissionGate();
        admission.Open();
        return new WindowsBackgroundSyncCoordinator(
            store,
            startup,
            owner,
            state,
            admission,
            transitions,
            AgentLocalizationTestFactory.CreateEnglish());
    }

    private static WindowsBackgroundSyncCoordinator CreateCoordinator(
        FakeBackgroundSyncSettingsStore store,
        FakeWindowsStartupRegistration startup,
        FakeWindowsAgentBackendRuntimeOwner owner,
        WindowsAgentLifecycleTransitionCoordinator transitions) =>
        new(
            store,
            startup,
            owner,
            new WindowsAgentStateStore(),
            new WindowsAgentAdmissionGate(),
            transitions,
            AgentLocalizationTestFactory.CreateEnglish());
}
