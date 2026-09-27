using Microsoft.Extensions.DependencyInjection;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using PasswordManagerLocal.Android.Runtime;
using PasswordManagerLocal.Common.Contracts.Endpoints;
using PasswordManagerLocal.Common.Backend.Exceptions;
using PasswordManagerLocal.Common.Backend.Hosting;
using PasswordManagerLocal.Common.Contracts.Runtime;
using PasswordManagerLocal.Common.Contracts.BackgroundSync;
using PasswordManagerLocal.Common.Tests.Fakes;
using PasswordManagerLocal.Common.Tests.TestInfrastructure;

namespace PasswordManagerLocal.Common.Tests.Android.Runtime;

[TestClass]
public sealed class AndroidRuntimeServiceHostTests
{

    [TestMethod]
    public async Task DisabledRestorationStopsStartedServiceWithoutCreatingRuntime()
    {
        using var fixture = CreateFixture(backgroundEnabled: false);
        await using var host = fixture.Host;

        await host.RestoreBackgroundStateAsync();

        Assert.AreEqual(0, fixture.Factory.CreateCalls);
        Assert.AreEqual(BackendLifetimeReason.None, fixture.Coordinator.ActiveReasons);
        Assert.IsFalse(host.Snapshot.HasRuntimeComposition);
        Assert.IsFalse(host.Snapshot.HasBackgroundLease);
        Assert.IsFalse(fixture.Platform.IsForeground);
        Assert.AreEqual(1, fixture.Platform.RequestStopCalls);
    }


    [TestMethod]
    public async Task RestorationSettingReadFailureRemainsDegradedAndCreatesNoRuntime()
    {
        using var fixture = CreateFixture(backgroundEnabled: true);
        fixture.Settings.ReadFailure = new IOException("settings unavailable");
        await using var host = fixture.Host;

        var state = await host.RestoreBackgroundStateAsync();

        Assert.IsTrue(state.IsDegraded);
        Assert.AreEqual(AndroidBackgroundSyncFailureKind.SettingRead, state.FailureKind);
        Assert.AreEqual(AndroidServiceStartPhase.RuntimeStartupFailed, state.ServiceStartPhase);
        Assert.IsFalse(state.IsRuntimeReady);
        Assert.IsFalse(state.IsForegroundActive);
        Assert.AreEqual(0, fixture.Factory.CreateCalls);
        Assert.AreEqual(BackendLifetimeReason.None, fixture.Coordinator.ActiveReasons);
        Assert.AreEqual(1, fixture.Platform.RequestStopCalls);
    }


    [TestMethod]
    public async Task TransientRestorationSettingReadFailureCanRecoverOnLaterStart()
    {
        using var fixture = CreateFixture(backgroundEnabled: true);
        fixture.Settings.ReadFailure = new IOException("settings unavailable");
        await using var host = fixture.Host;

        var failed = await host.RestoreBackgroundStateAsync();
        fixture.Settings.ReadFailure = null;
        var recovered = await host.RestoreBackgroundStateAsync();

        Assert.AreEqual(AndroidBackgroundSyncFailureKind.SettingRead, failed.FailureKind);
        Assert.IsTrue(recovered.IsOperational);
        Assert.AreEqual(2, fixture.Settings.ReadCalls);
        Assert.AreEqual(1, fixture.Factory.CreateCalls);
        Assert.AreEqual(BackendLifetimeReason.BackgroundSync, fixture.Coordinator.ActiveReasons);
    }

    [TestMethod]
    public async Task EnabledRestorationStartsForegroundAndAcquiresOneBackgroundLease()
    {
        using var fixture = CreateFixture(backgroundEnabled: true);
        await using var host = fixture.Host;

        await host.RestoreBackgroundStateAsync();

        Assert.AreEqual(1, fixture.Factory.CreateCalls);
        Assert.AreEqual(1, fixture.Platform.EnsureServiceStartedCalls);
        Assert.AreEqual(1, fixture.Platform.EnterForegroundCalls);
        Assert.AreEqual(BackendLifetimeReason.BackgroundSync, fixture.Coordinator.ActiveReasons);
        Assert.IsTrue(host.Snapshot.HasBackgroundLease);
        Assert.IsTrue(host.Snapshot.IsForeground);
        Assert.AreEqual(
            AndroidForegroundNotificationState.BackgroundSynchronizationActive,
            fixture.Platform.LastNotificationState);
    }

    [TestMethod]
    public async Task RestorationRuntimeStartupFailureReportsDegradedAndCleansPartialComposition()
    {
        using var fixture = CreateFixture(backgroundEnabled: true);
        fixture.Runtime.StartupFailure = new InvalidOperationException("runtime startup failed");
        await using var host = fixture.Host;

        await host.RestoreBackgroundStateAsync();
        var state = await host.GetBackgroundStateAsync();

        Assert.IsTrue(state.IsEnabled);
        Assert.IsTrue(state.IsDegraded);
        Assert.AreEqual(AndroidBackgroundSyncFailureKind.RuntimeLease, state.FailureKind);
        Assert.AreEqual(1, fixture.Factory.CreateCalls);
        Assert.AreEqual(1, fixture.Runtime.DisposeCalls);
        Assert.AreEqual(BackendLifetimeReason.None, fixture.Coordinator.ActiveReasons);
        Assert.IsFalse(host.Snapshot.HasRuntimeComposition);
        Assert.IsFalse(fixture.Platform.IsForeground);
        Assert.IsFalse(fixture.Platform.IsServiceStarted);
    }

    [TestMethod]
    public async Task RepeatedRestorationDoesNotDuplicateRuntimeOrBackgroundLease()
    {
        using var fixture = CreateFixture(backgroundEnabled: true);
        await using var host = fixture.Host;

        await host.RestoreBackgroundStateAsync();
        await host.RestoreBackgroundStateAsync();

        Assert.AreEqual(1, fixture.Factory.CreateCalls);
        Assert.AreEqual(1, fixture.Runtime.EnsureStartedCalls);
        Assert.AreEqual(1, fixture.Platform.EnsureServiceStartedCalls);
        Assert.AreEqual(1, fixture.Platform.EnterForegroundCalls);
        Assert.AreEqual(BackendLifetimeReason.BackgroundSync, fixture.Coordinator.ActiveReasons);
    }


    [TestMethod]
    public async Task BackgroundDisableWithoutInteractiveStopsRuntimeForegroundAndService()
    {
        using var fixture = CreateFixture(backgroundEnabled: true);
        await using var host = fixture.Host;
        await host.RestoreBackgroundStateAsync();

        var state = await host.SetBackgroundEnabledFromServiceAsync(false);

        Assert.IsFalse(state.IsEnabled);
        Assert.AreEqual(BackendLifetimeReason.None, fixture.Coordinator.ActiveReasons);
        Assert.AreEqual(1, fixture.Runtime.StopCalls);
        Assert.AreEqual(1, fixture.Runtime.DisposeCalls);
        Assert.IsFalse(fixture.Platform.IsForeground);
        Assert.IsFalse(fixture.Platform.IsServiceStarted);
        Assert.IsFalse(host.Snapshot.HasRuntimeComposition);
    }


    [TestMethod]
    public async Task StopFailureStillAttemptsCompositionDisposalAndServiceStop()
    {
        using var fixture = CreateFixture(backgroundEnabled: true);
        await using var host = fixture.Host;
        await host.RestoreBackgroundStateAsync();
        fixture.Runtime.StopFailure = new IOException("runtime stop failed");

        var state = await host.SetBackgroundEnabledFromServiceAsync(false);

        Assert.IsFalse(state.IsEnabled);
        Assert.IsTrue(state.IsDegraded);
        Assert.AreEqual(AndroidBackgroundSyncFailureKind.Shutdown, state.FailureKind);
        Assert.AreEqual(BackendLifetimeReason.None, fixture.Coordinator.ActiveReasons);
        Assert.AreEqual(1, fixture.Runtime.DisposeCalls);
        Assert.IsFalse(host.Snapshot.HasRuntimeComposition);
        Assert.IsFalse(fixture.Platform.IsForeground);
        Assert.IsFalse(fixture.Platform.IsServiceStarted);
    }

    [TestMethod]
    public async Task ActivityReopenReusesBackgroundOwnedRuntimeWithFreshAdapter()
    {
        using var fixture = CreateFixture(backgroundEnabled: true);
        await using var host = fixture.Host;
        await host.RestoreBackgroundStateAsync();
        var first = await host.AttachInteractiveClientAsync();
        await first.DisposeAsync();

        var second = await host.AttachInteractiveClientAsync();

        Assert.AreEqual(1, fixture.Factory.CreateCalls);
        Assert.AreEqual(
            BackendLifetimeReason.InteractiveUi | BackendLifetimeReason.BackgroundSync,
            fixture.Coordinator.ActiveReasons);
        Assert.IsInstanceOfType<AndroidAttachmentAuthorizedEndpoints>(await second.GetEndpointsAsync());
        await second.DisposeAsync();
    }

    [TestMethod]
    public async Task InteractiveAttachmentPreservesForegroundFailureCategoryWhileRemainingUsable()
    {
        using var fixture = CreateFixture(backgroundEnabled: true);
        fixture.Platform.ForegroundEntryResult = new AndroidForegroundEntryResult(
            IsForegroundEntered: false,
            AndroidNotificationAvailability.ChannelDisabled,
            AndroidForegroundEntryFailureKind.ForegroundStartRejected,
            RequiresUserAction: true,
            "Foreground start rejected.");
        await using var host = fixture.Host;

        await using var client = await host.AttachInteractiveClientAsync();
        var state = await host.GetBackgroundStateFromAttachmentAsync(client);

        Assert.IsTrue(state.IsEnabled);
        Assert.IsTrue(state.IsDegraded);
        Assert.AreEqual(AndroidBackgroundSyncFailureKind.ForegroundService, state.FailureKind);
        Assert.AreEqual(AndroidServiceStartPhase.ForegroundStartRejected, state.ServiceStartPhase);
        Assert.AreEqual(AndroidNotificationAvailability.ChannelDisabled, state.NotificationAvailability);
        Assert.IsTrue(state.RequiresUserAction);
        Assert.IsFalse(state.IsBackgroundLeaseActive);
        Assert.AreEqual(BackendLifetimeReason.InteractiveUi, fixture.Coordinator.ActiveReasons);
        Assert.IsTrue(host.Snapshot.HasInteractiveAttachment);
    }

    [TestMethod]
    public async Task InteractiveDetachPreservesBackgroundRuntime()
    {
        using var fixture = CreateFixture(backgroundEnabled: true);
        await using var host = fixture.Host;
        var client = await host.AttachInteractiveClientAsync();

        Assert.AreEqual(
            BackendLifetimeReason.InteractiveUi | BackendLifetimeReason.BackgroundSync,
            fixture.Coordinator.ActiveReasons);

        await client.DisposeAsync();

        Assert.AreEqual(BackendLifetimeReason.BackgroundSync, fixture.Coordinator.ActiveReasons);
        Assert.AreEqual(0, fixture.Runtime.DisposeCalls);
        Assert.IsTrue(host.Snapshot.HasRuntimeComposition);
    }

    [TestMethod]
    public async Task InteractiveDetachWithoutBackgroundStopsAndDisposesRuntime()
    {
        using var fixture = CreateFixture(backgroundEnabled: false);
        await using var host = fixture.Host;
        var client = await host.AttachInteractiveClientAsync();

        await client.DisposeAsync();

        Assert.AreEqual(BackendLifetimeReason.None, fixture.Coordinator.ActiveReasons);
        Assert.AreEqual(1, fixture.Runtime.StopCalls);
        Assert.AreEqual(1, fixture.Runtime.DisposeCalls);
        Assert.IsFalse(host.Snapshot.HasRuntimeComposition);
        Assert.AreEqual(1, fixture.Platform.RequestStopCalls);
    }

    [TestMethod]
    public async Task ConcurrentInteractiveAttachmentUsesLastAttachmentWinsPolicy()
    {
        using var fixture = CreateFixture(backgroundEnabled: false);
        await using var host = fixture.Host;
        var first = await host.AttachInteractiveClientAsync();

        var second = await host.AttachInteractiveClientAsync();

        Assert.Throws<ObjectDisposedException>(() => _ = first.Snapshot);
        Assert.AreEqual(1, fixture.Factory.CreateCalls);
        Assert.AreEqual(BackendLifetimeReason.InteractiveUi, fixture.Coordinator.ActiveReasons);
        Assert.IsInstanceOfType<AndroidAttachmentAuthorizedEndpoints>(await second.GetEndpointsAsync());
        await first.DisposeAsync();
        await second.DisposeAsync();
    }

    [TestMethod]
    public async Task EnableAndDisableWhileInteractiveRetainsInteractiveLease()
    {
        using var fixture = CreateFixture(backgroundEnabled: false);
        await using var host = fixture.Host;
        var client = await host.AttachInteractiveClientAsync();

        var enabled = await host.SetBackgroundEnabledFromServiceAsync(true);
        Assert.IsTrue(enabled.IsEnabled);
        Assert.AreEqual(
            BackendLifetimeReason.InteractiveUi | BackendLifetimeReason.BackgroundSync,
            fixture.Coordinator.ActiveReasons);

        var disabled = await host.SetBackgroundEnabledFromServiceAsync(false);
        Assert.IsFalse(disabled.IsEnabled);
        Assert.AreEqual(BackendLifetimeReason.InteractiveUi, fixture.Coordinator.ActiveReasons);
        Assert.IsTrue(host.Snapshot.HasRuntimeComposition);
        Assert.IsFalse(host.Snapshot.IsForeground);

        await client.DisposeAsync();
    }

    [TestMethod]
    public async Task SecureStorageUnavailableDefersBackgroundRuntimeUntilUnlock()
    {
        using var fixture = CreateFixture(backgroundEnabled: true, secureStorageAvailable: false);
        await using var host = fixture.Host;

        await host.RestoreBackgroundStateAsync();
        var deferred = await host.GetBackgroundStateAsync();

        Assert.IsTrue(deferred.IsEnabled);
        Assert.IsTrue(deferred.IsSecureStorageDeferred);
        Assert.AreEqual(AndroidServiceStartPhase.DeferredUntilUnlock, deferred.ServiceStartPhase);
        Assert.IsTrue(deferred.IsForegroundActive);
        Assert.AreEqual(0, fixture.Settings.ReadCalls);
        Assert.AreEqual(0, fixture.Factory.CreateCalls);
        Assert.AreEqual(BackendLifetimeReason.None, fixture.Coordinator.ActiveReasons);

        fixture.SecureStorage.IsAvailable = true;
        await host.RestoreBackgroundStateAsync();

        Assert.AreEqual(1, fixture.Factory.CreateCalls);
        Assert.AreEqual(BackendLifetimeReason.BackgroundSync, fixture.Coordinator.ActiveReasons);
    }

    [TestMethod]
    public async Task InteractiveAttachmentDoesNotReadSettingsBeforeSecureStorageIsAvailable()
    {
        using var fixture = CreateFixture(backgroundEnabled: true, secureStorageAvailable: false);
        await using var host = fixture.Host;

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => host.AttachInteractiveClientAsync());

        Assert.AreEqual(0, fixture.Settings.ReadCalls);
        Assert.AreEqual(0, fixture.Settings.WriteCalls);
        Assert.AreEqual(0, fixture.Factory.CreateCalls);
        Assert.IsFalse(host.Snapshot.HasInteractiveAttachment);
    }

    [TestMethod]
    public async Task SettingMutationDoesNotReadOrWriteBeforeSecureStorageIsAvailable()
    {
        using var fixture = CreateFixture(backgroundEnabled: false, secureStorageAvailable: false);
        await using var host = fixture.Host;

        var state = await host.SetBackgroundEnabledFromServiceAsync(true);

        Assert.IsFalse(state.IsAvailable);
        Assert.IsTrue(state.IsDegraded);
        Assert.AreEqual(AndroidBackgroundSyncFailureKind.SecureStorageDeferred, state.FailureKind);
        Assert.AreEqual(AndroidServiceStartPhase.DeferredUntilUnlock, state.ServiceStartPhase);
        Assert.AreEqual(0, fixture.Settings.ReadCalls);
        Assert.AreEqual(0, fixture.Settings.WriteCalls);
        Assert.AreEqual(0, fixture.Factory.CreateCalls);
    }

    [TestMethod]
    public async Task DisabledNotificationsProduceTruthfulDegradedEnabledState()
    {
        using var fixture = CreateFixture(backgroundEnabled: false);
        fixture.Platform.AreNotificationsEnabled = false;
        await using var host = fixture.Host;

        var state = await host.SetBackgroundEnabledFromServiceAsync(true);

        Assert.IsTrue(state.IsEnabled);
        Assert.IsTrue(state.IsDegraded);
        Assert.AreEqual(AndroidBackgroundSyncFailureKind.ForegroundService, state.FailureKind);
        Assert.IsTrue(state.IsBackgroundLeaseActive);
        Assert.IsFalse(state.IsOperational);
        Assert.IsTrue(state.RequiresUserAction);
        Assert.AreEqual(
            AndroidNotificationAvailability.ApplicationDisabled,
            state.NotificationAvailability);
    }


    [TestMethod]
    public async Task ForegroundStartFailurePreservesEnabledSettingWithoutCreatingRuntime()
    {
        using var fixture = CreateFixture(backgroundEnabled: false);
        fixture.Platform.EnterForegroundFailure = new InvalidOperationException("foreground denied");
        await using var host = fixture.Host;

        var state = await host.SetBackgroundEnabledFromServiceAsync(true);

        Assert.IsTrue(state.IsEnabled);
        Assert.IsTrue(fixture.Settings.Current.IsEnabled);
        Assert.IsTrue(state.IsDegraded);
        Assert.AreEqual(AndroidBackgroundSyncFailureKind.RuntimeLease, state.FailureKind);
        Assert.AreEqual(1, fixture.Settings.WriteCalls);
        Assert.AreEqual(0, fixture.Factory.CreateCalls);
        Assert.AreEqual(BackendLifetimeReason.None, fixture.Coordinator.ActiveReasons);
        Assert.IsFalse(fixture.Platform.IsForeground);
    }


    [TestMethod]
    public async Task RuntimeStartupFailurePreservesEnabledSettingAndCleansPartialComposition()
    {
        using var fixture = CreateFixture(backgroundEnabled: false);
        fixture.Runtime.StartupFailure = new InvalidOperationException("runtime startup failed");
        await using var host = fixture.Host;

        var state = await host.SetBackgroundEnabledFromServiceAsync(true);

        Assert.IsTrue(state.IsEnabled);
        Assert.IsTrue(fixture.Settings.Current.IsEnabled);
        Assert.IsTrue(state.IsDegraded);
        Assert.AreEqual(AndroidBackgroundSyncFailureKind.RuntimeLease, state.FailureKind);
        Assert.AreEqual(1, fixture.Settings.WriteCalls);
        Assert.AreEqual(1, fixture.Factory.CreateCalls);
        Assert.AreEqual(1, fixture.Runtime.DisposeCalls);
        Assert.AreEqual(BackendLifetimeReason.None, fixture.Coordinator.ActiveReasons);
        Assert.IsFalse(host.Snapshot.HasRuntimeComposition);
        Assert.IsFalse(fixture.Platform.IsForeground);
    }

    [TestMethod]
    public async Task SettingPersistenceFailureReportsTruthfulPreviousState()
    {
        using var fixture = CreateFixture(backgroundEnabled: false);
        fixture.Settings.WriteFailure = new IOException("settings unavailable");
        await using var host = fixture.Host;

        var state = await host.SetBackgroundEnabledFromServiceAsync(true);

        Assert.IsFalse(state.IsEnabled);
        Assert.AreEqual(AndroidBackgroundSyncFailureKind.SettingPersistence, state.FailureKind);
        Assert.AreEqual(0, fixture.Factory.CreateCalls);
    }

    [TestMethod]
    public async Task EnableWriteExceptionWithAuthoritativeEnabledReadbackStillStartsBackgroundRuntime()
    {
        using var fixture = CreateFixture(backgroundEnabled: false);
        fixture.Settings.WriteFailure = new IOException("write reported failure after commit");
        fixture.Settings.CommitBeforeWriteFailure = true;
        await using var host = fixture.Host;

        var state = await host.SetBackgroundEnabledFromServiceAsync(true);

        Assert.IsTrue(state.IsEnabled);
        Assert.IsFalse(state.IsDegraded);
        Assert.AreEqual(AndroidBackgroundSyncFailureKind.None, state.FailureKind);
        Assert.AreEqual(BackendLifetimeReason.BackgroundSync, fixture.Coordinator.ActiveReasons);
    }

    [TestMethod]
    public async Task DisableWriteExceptionWithAuthoritativeDisabledReadbackStillStopsBackgroundRuntime()
    {
        using var fixture = CreateFixture(backgroundEnabled: true);
        await using var host = fixture.Host;
        await host.RestoreBackgroundStateAsync();
        fixture.Settings.WriteFailure = new IOException("write reported failure after commit");
        fixture.Settings.CommitBeforeWriteFailure = true;

        var state = await host.SetBackgroundEnabledFromServiceAsync(false);

        Assert.IsFalse(state.IsEnabled);
        Assert.IsFalse(state.IsDegraded);
        Assert.AreEqual(AndroidBackgroundSyncFailureKind.None, state.FailureKind);
        Assert.AreEqual(BackendLifetimeReason.None, fixture.Coordinator.ActiveReasons);
        Assert.IsFalse(host.Snapshot.HasRuntimeComposition);
    }

    [TestMethod]
    public async Task SettingWriteAndReadbackFailureReportsUncertainOutcome()
    {
        using var fixture = CreateFixture(backgroundEnabled: false);
        await using var host = fixture.Host;
        await host.GetBackgroundStateAsync();
        fixture.Settings.WriteFailure = new IOException("settings write failed");
        fixture.Settings.ReadFailure = new IOException("settings readback failed");

        var state = await host.SetBackgroundEnabledFromServiceAsync(true);

        Assert.IsTrue(state.IsDegraded);
        Assert.AreEqual(AndroidBackgroundSyncFailureKind.Rollback, state.FailureKind);
        Assert.AreEqual(BackendLifetimeReason.None, fixture.Coordinator.ActiveReasons);
        Assert.AreEqual(0, fixture.Factory.CreateCalls);
    }

    [TestMethod]
    public async Task DatabaseResetRestoresInteractiveAndBackgroundLeases()
    {
        using var fixture = CreateFixture(backgroundEnabled: true);
        await using var host = fixture.Host;
        var client = await host.AttachInteractiveClientAsync();

        await client.ResetDatabaseAndRestartAsync();

        Assert.AreEqual(1, fixture.Runtime.ResetCalls);
        Assert.AreEqual(
            BackendLifetimeReason.InteractiveUi | BackendLifetimeReason.BackgroundSync,
            fixture.Coordinator.ActiveReasons);
        Assert.IsInstanceOfType<AndroidAttachmentAuthorizedEndpoints>(await client.GetEndpointsAsync());
        await client.DisposeAsync();
    }

    [TestMethod]
    public async Task DatabaseCompatibilityFailureKeepsResetCapableRecoveryAttachment()
    {
        using var fixture = CreateFixture(backgroundEnabled: false);
        var compatibilityFailure = new DatabaseVersionNotSupportedException(99, 1, 1);
        fixture.Runtime.StartupFailure = compatibilityFailure;
        fixture.Runtime.BeforeReset = () => fixture.Runtime.StartupFailure = null;
        await using var host = fixture.Host;

        var client = await host.AttachInteractiveClientAsync();

        Assert.AreEqual(
            AndroidInteractiveAttachmentState.DatabaseRecovery,
            client.AttachmentState);
        await Assert.ThrowsExactlyAsync<DatabaseVersionNotSupportedException>(
            () => client.ConnectAsync());
        await Assert.ThrowsAsync<InvalidOperationException>(
            () => client.GetEndpointsAsync());

        await client.ResetDatabaseAndRestartAsync();

        Assert.AreEqual(AndroidInteractiveAttachmentState.Active, client.AttachmentState);
        Assert.AreEqual(1, fixture.Runtime.ResetCalls);
        Assert.IsInstanceOfType<AndroidAttachmentAuthorizedEndpoints>(
            await client.GetEndpointsAsync());
        await client.DisposeAsync();
    }


    [TestMethod]
    public async Task DatabaseResetReestablishesForegroundBeforeRestoringEnabledBackgroundLease()
    {
        using var fixture = CreateFixture(backgroundEnabled: true);
        fixture.Platform.ForegroundEntryResult = new AndroidForegroundEntryResult(
            IsForegroundEntered: false,
            AndroidNotificationAvailability.ChannelDisabled,
            AndroidForegroundEntryFailureKind.ForegroundStartRejected,
            RequiresUserAction: true,
            "Foreground start rejected.");
        await using var host = fixture.Host;
        var client = await host.AttachInteractiveClientAsync();

        Assert.IsFalse(host.Snapshot.IsForeground);
        Assert.IsFalse(host.Snapshot.HasBackgroundLease);
        fixture.Platform.ForegroundEntryResult = null;

        await client.ResetDatabaseAndRestartAsync();

        Assert.IsTrue(host.Snapshot.IsForeground);
        Assert.IsTrue(host.Snapshot.HasBackgroundLease);
        Assert.AreEqual(2, fixture.Platform.EnterForegroundCalls);
        Assert.AreEqual(
            BackendLifetimeReason.InteractiveUi | BackendLifetimeReason.BackgroundSync,
            fixture.Coordinator.ActiveReasons);
        await client.DisposeAsync();
    }

    [TestMethod]
    public async Task DatabaseResetPreservesForegroundFailureCategoryWhenBackgroundRestoreIsRejected()
    {
        using var fixture = CreateFixture(backgroundEnabled: true);
        fixture.Platform.ForegroundEntryResult = new AndroidForegroundEntryResult(
            IsForegroundEntered: false,
            AndroidNotificationAvailability.ChannelDisabled,
            AndroidForegroundEntryFailureKind.ForegroundStartRejected,
            RequiresUserAction: true,
            "Foreground start rejected.");
        await using var host = fixture.Host;
        var client = await host.AttachInteractiveClientAsync();

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => client.ResetDatabaseAndRestartAsync());

        var state = await host.GetBackgroundStateFromAttachmentAsync(client);
        Assert.IsTrue(state.IsEnabled);
        Assert.IsFalse(state.IsOperational);
        Assert.AreEqual(AndroidBackgroundSyncFailureKind.ForegroundService, state.FailureKind);
        Assert.AreEqual(AndroidServiceStartPhase.ForegroundStartRejected, state.ServiceStartPhase);
        Assert.AreEqual(AndroidNotificationAvailability.ChannelDisabled, state.NotificationAvailability);
        Assert.IsTrue(state.RequiresUserAction);
        Assert.IsFalse(host.Snapshot.HasBackgroundLease);
        await client.DisposeAsync();
    }

    [TestMethod]
    public async Task ResetFailureDoesNotConstructReplacementRuntime()
    {
        using var fixture = CreateFixture(backgroundEnabled: true);
        await using var host = fixture.Host;
        var client = await host.AttachInteractiveClientAsync();
        fixture.Runtime.ResetFailure = new IOException("reset failed");

        await Assert.ThrowsAsync<IOException>(
            () => client.ResetDatabaseAndRestartAsync());

        Assert.AreEqual(1, fixture.Factory.CreateCalls);
        Assert.AreEqual(1, fixture.Runtime.ResetCalls);
        Assert.AreEqual(BackendLifetimeReason.None, fixture.Coordinator.ActiveReasons);
        Assert.IsFalse(host.Snapshot.HasBackgroundLease);
        Assert.IsTrue(host.Snapshot.HasRuntimeComposition);
        await client.DisposeAsync();
    }


    [TestMethod]
    public async Task EndpointAuthorityIsSuspendedWhileDatabaseResetOwnsTheTransition()
    {
        using var fixture = CreateFixture(backgroundEnabled: true);
        await using var host = fixture.Host;
        var client = await host.AttachInteractiveClientAsync();
        var endpoints = await client.GetEndpointsAsync();
        var resetEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var resetRelease = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        fixture.Runtime.ResetEntered = resetEntered;
        fixture.Runtime.ResetRelease = resetRelease.Task;

        var resetTask = client.ResetDatabaseAndRestartAsync();
        await resetEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));

        try
        {
            Assert.AreEqual(AndroidInteractiveAttachmentState.Resetting, client.AttachmentState);
            await Assert.ThrowsAsync<InvalidOperationException>(
                () => endpoints.GetLocalDeviceInfoAsync());
        }
        finally
        {
            resetRelease.TrySetResult();
        }

        await resetTask.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.AreEqual(AndroidInteractiveAttachmentState.Active, client.AttachmentState);
        Assert.IsInstanceOfType<AndroidAttachmentAuthorizedEndpoints>(
            await client.GetEndpointsAsync());
        await client.DisposeAsync();
    }

    [TestMethod]
    public async Task ActivityReplacementWaitsForAdmittedDatabaseReset()
    {
        using var fixture = CreateFixture(backgroundEnabled: true);
        await using var host = fixture.Host;
        var first = await host.AttachInteractiveClientAsync();
        var staleEndpoints = await first.GetEndpointsAsync();
        var resetEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var resetRelease = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        fixture.Runtime.ResetEntered = resetEntered;
        fixture.Runtime.ResetRelease = resetRelease.Task;

        var resetTask = first.ResetDatabaseAndRestartAsync();
        await resetEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var replacementTask = host.AttachInteractiveClientAsync();
        Assert.IsFalse(replacementTask.IsCompleted);

        resetRelease.TrySetResult();
        await resetTask.WaitAsync(TimeSpan.FromSeconds(5));
        var second = await replacementTask.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.AreEqual(AndroidInteractiveAttachmentState.Disposed, first.AttachmentState);
        await Assert.ThrowsAsync<InvalidOperationException>(
            () => staleEndpoints.GetLocalDeviceInfoAsync());
        Assert.IsInstanceOfType<AndroidAttachmentAuthorizedEndpoints>(
            await second.GetEndpointsAsync());
        await second.DisposeAsync();
    }

    [TestMethod]
    public async Task BackgroundSettingChangeWaitsForAdmittedDatabaseReset()
    {
        using var fixture = CreateFixture(backgroundEnabled: true);
        await using var host = fixture.Host;
        var client = await host.AttachInteractiveClientAsync();
        var resetEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var resetRelease = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        fixture.Runtime.ResetEntered = resetEntered;
        fixture.Runtime.ResetRelease = resetRelease.Task;

        var resetTask = client.ResetDatabaseAndRestartAsync();
        await resetEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var disableTask = host.SetBackgroundEnabledFromAttachmentAsync(client, false);
        Assert.IsFalse(disableTask.IsCompleted);

        resetRelease.TrySetResult();
        await resetTask.WaitAsync(TimeSpan.FromSeconds(5));
        var state = await disableTask.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.IsFalse(state.IsEnabled);
        Assert.IsFalse(host.Snapshot.HasBackgroundLease);
        Assert.AreEqual(BackendLifetimeReason.InteractiveUi, fixture.Coordinator.ActiveReasons);
        await client.DisposeAsync();
    }

    [TestMethod]
    public async Task ServiceDisposalWaitsForAdmittedDatabaseResetAndDisposesOnce()
    {
        using var fixture = CreateFixture(backgroundEnabled: true);
        var host = fixture.Host;
        var client = await host.AttachInteractiveClientAsync();
        var resetEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var resetRelease = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        fixture.Runtime.ResetEntered = resetEntered;
        fixture.Runtime.ResetRelease = resetRelease.Task;

        var resetTask = client.ResetDatabaseAndRestartAsync();
        await resetEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var disposeTask = host.DisposeAsync().AsTask();
        Assert.IsFalse(disposeTask.IsCompleted);

        resetRelease.TrySetResult();
        await resetTask.WaitAsync(TimeSpan.FromSeconds(5));
        await disposeTask.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.IsTrue(host.Snapshot.IsDisposed);
        Assert.AreEqual(AndroidInteractiveAttachmentState.Disposed, client.AttachmentState);
        Assert.AreEqual(1, fixture.Runtime.DisposeCalls);
    }

    [TestMethod]
    public async Task ServiceDestructionCleanupDoesNotInvokeDestroyedServiceController()
    {
        using var fixture = CreateFixture(backgroundEnabled: true);
        var host = fixture.Host;
        await host.RestoreBackgroundStateAsync();
        fixture.Platform.ExitForeground();
        var exitCallsBeforeCleanup = fixture.Platform.ExitForegroundCalls;
        var stopCallsBeforeCleanup = fixture.Platform.RequestStopCalls;

        await host.DisposeRuntimeResourcesAsync();

        Assert.IsTrue(host.Snapshot.IsDisposed);
        Assert.AreEqual(exitCallsBeforeCleanup, fixture.Platform.ExitForegroundCalls);
        Assert.AreEqual(stopCallsBeforeCleanup, fixture.Platform.RequestStopCalls);
        Assert.AreEqual(BackendLifetimeReason.None, fixture.Coordinator.ActiveReasons);
        Assert.AreEqual(1, fixture.Runtime.DisposeCalls);
    }


    [TestMethod]
    public async Task SafeResetFailureRestoresCurrentAttachmentAuthorityForReconnect()
    {
        using var fixture = CreateFixture(backgroundEnabled: true);
        await using var host = fixture.Host;
        var client = await host.AttachInteractiveClientAsync();
        fixture.Runtime.ResetFailure = new IOException("reset failed");

        await Assert.ThrowsAsync<IOException>(
            () => client.ResetDatabaseAndRestartAsync());

        Assert.AreEqual(AndroidInteractiveAttachmentState.Active, client.AttachmentState);
        await client.ConnectAsync();
        Assert.IsInstanceOfType<AndroidAttachmentAuthorizedEndpoints>(
            await client.GetEndpointsAsync());
        await client.DisposeAsync();
    }

    [TestMethod]
    public async Task RepeatedDisposalDisposesRuntimeExactlyOnce()
    {
        using var fixture = CreateFixture(backgroundEnabled: false);
        var client = await fixture.Host.AttachInteractiveClientAsync();
        await client.DisposeAsync();

        await fixture.Host.DisposeAsync();
        await fixture.Host.DisposeAsync();

        Assert.AreEqual(1, fixture.Runtime.DisposeCalls);
        Assert.IsTrue(fixture.Host.Snapshot.IsDisposed);
    }

    [TestMethod]
    public async Task CurrentAttachmentCanChangeBackgroundSetting()
    {
        using var fixture = CreateFixture(backgroundEnabled: false);
        await using var host = fixture.Host;
        var client = await host.AttachInteractiveClientAsync();

        var state = await host.SetBackgroundEnabledFromAttachmentAsync(client, true);

        Assert.IsTrue(state.IsEnabled);
        Assert.AreEqual(AndroidBackgroundSyncFailureKind.None, state.FailureKind);
        Assert.AreEqual(1, fixture.Settings.WriteCalls);
        Assert.AreEqual(
            BackendLifetimeReason.InteractiveUi | BackendLifetimeReason.BackgroundSync,
            fixture.Coordinator.ActiveReasons);
        await client.DisposeAsync();
    }

    [TestMethod]
    public async Task SupersededAttachmentCannotEnableBackgroundBeforeAnyEffects()
    {
        using var fixture = CreateFixture(backgroundEnabled: false);
        await using var host = fixture.Host;
        var first = await host.AttachInteractiveClientAsync();
        var second = await host.AttachInteractiveClientAsync();
        var writeCalls = fixture.Settings.WriteCalls;
        var foregroundCalls = fixture.Platform.EnterForegroundCalls;

        var state = await host.SetBackgroundEnabledFromAttachmentAsync(first, true);

        Assert.AreEqual(AndroidBackgroundSyncFailureKind.AttachmentAuthority, state.FailureKind);
        Assert.IsFalse(fixture.Settings.Current.IsEnabled);
        Assert.AreEqual(writeCalls, fixture.Settings.WriteCalls);
        Assert.AreEqual(foregroundCalls, fixture.Platform.EnterForegroundCalls);
        Assert.AreEqual(BackendLifetimeReason.InteractiveUi, fixture.Coordinator.ActiveReasons);
        await second.DisposeAsync();
    }

    [TestMethod]
    public async Task SupersededAttachmentCannotDisableExistingBackgroundLease()
    {
        using var fixture = CreateFixture(backgroundEnabled: true);
        await using var host = fixture.Host;
        var first = await host.AttachInteractiveClientAsync();
        var second = await host.AttachInteractiveClientAsync();
        var writeCalls = fixture.Settings.WriteCalls;
        var exitCalls = fixture.Platform.ExitForegroundCalls;

        var state = await host.SetBackgroundEnabledFromAttachmentAsync(first, false);

        Assert.AreEqual(AndroidBackgroundSyncFailureKind.AttachmentAuthority, state.FailureKind);
        Assert.IsTrue(fixture.Settings.Current.IsEnabled);
        Assert.AreEqual(writeCalls, fixture.Settings.WriteCalls);
        Assert.AreEqual(exitCalls, fixture.Platform.ExitForegroundCalls);
        Assert.AreEqual(
            BackendLifetimeReason.InteractiveUi | BackendLifetimeReason.BackgroundSync,
            fixture.Coordinator.ActiveReasons);
        await second.DisposeAsync();
    }

    [TestMethod]
    public async Task DisposedAttachmentCannotChangeBackgroundSetting()
    {
        using var fixture = CreateFixture(backgroundEnabled: false);
        await using var host = fixture.Host;
        var client = await host.AttachInteractiveClientAsync();
        await client.DisposeAsync();
        var writeCalls = fixture.Settings.WriteCalls;

        var state = await host.SetBackgroundEnabledFromAttachmentAsync(client, true);

        Assert.AreEqual(AndroidBackgroundSyncFailureKind.AttachmentAuthority, state.FailureKind);
        Assert.AreEqual(writeCalls, fixture.Settings.WriteCalls);
        Assert.IsFalse(fixture.Settings.Current.IsEnabled);
    }

    [TestMethod]
    public async Task ClosingAttachmentRejectsMutationBeforePersistence()
    {
        using var fixture = CreateFixture(backgroundEnabled: false);
        await using var host = fixture.Host;
        var client = await host.AttachInteractiveClientAsync();
        var disposeEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var disposeRelease = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        fixture.Runtime.InteractiveSessionDisposeEntered = disposeEntered;
        fixture.Runtime.InteractiveSessionDisposeRelease = disposeRelease.Task;
        var writeCalls = fixture.Settings.WriteCalls;

        var disposeTask = client.DisposeAsync().AsTask();
        await disposeEntered.Task;
        var mutationTask = host.SetBackgroundEnabledFromAttachmentAsync(client, true);
        disposeRelease.TrySetResult();

        await disposeTask;
        var state = await mutationTask;
        Assert.AreEqual(AndroidBackgroundSyncFailureKind.AttachmentAuthority, state.FailureKind);
        Assert.AreEqual(writeCalls, fixture.Settings.WriteCalls);
    }

    [TestMethod]
    public async Task ServiceRestorationNeedsNoActivityAuthority()
    {
        using var fixture = CreateFixture(backgroundEnabled: true);
        await using var host = fixture.Host;

        await host.RestoreBackgroundStateAsync();

        Assert.IsFalse(host.Snapshot.HasInteractiveAttachment);
        Assert.IsTrue(host.Snapshot.HasBackgroundLease);
        Assert.IsTrue(host.Snapshot.AcceptsInteractiveAttachments);
    }

    [TestMethod]
    public async Task ConcurrentReplacementLeavesExactlyOneAuthoritativeAttachment()
    {
        using var fixture = CreateFixture(backgroundEnabled: false);
        await using var host = fixture.Host;
        var first = await host.AttachInteractiveClientAsync();
        var disposeEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var disposeRelease = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        fixture.Runtime.InteractiveSessionDisposeEntered = disposeEntered;
        fixture.Runtime.InteractiveSessionDisposeRelease = disposeRelease.Task;

        var secondTask = host.AttachInteractiveClientAsync();
        await disposeEntered.Task;
        var thirdTask = host.AttachInteractiveClientAsync();
        disposeRelease.TrySetResult();
        var second = await secondTask;
        var third = await thirdTask;

        var firstResult = await host.SetBackgroundEnabledFromAttachmentAsync(first, true);
        var secondResult = await host.SetBackgroundEnabledFromAttachmentAsync(second, true);
        var thirdResult = await host.SetBackgroundEnabledFromAttachmentAsync(third, true);

        Assert.AreEqual(AndroidBackgroundSyncFailureKind.AttachmentAuthority, firstResult.FailureKind);
        Assert.AreEqual(AndroidBackgroundSyncFailureKind.AttachmentAuthority, secondResult.FailureKind);
        Assert.AreEqual(AndroidBackgroundSyncFailureKind.None, thirdResult.FailureKind);
        Assert.IsTrue(fixture.Settings.Current.IsEnabled);
        await third.DisposeAsync();
    }

    [TestMethod]
    public async Task CleanupFailureWithSuccessfulRecoveryKeepsBackgroundRuntimeSafe()
    {
        using var fixture = CreateFixture(backgroundEnabled: true);
        await using var host = fixture.Host;
        var client = await host.AttachInteractiveClientAsync();
        fixture.Runtime.InteractiveSessionDisposeFailure = new IOException("interactive cleanup failed");

        await client.DisposeAsync();

        Assert.IsFalse(host.Snapshot.IsRuntimeUnsafe);
        Assert.IsTrue(host.Snapshot.HasBackgroundLease);
        Assert.AreEqual(BackendLifetimeReason.BackgroundSync, fixture.Coordinator.ActiveReasons);
        Assert.IsTrue(fixture.Platform.IsForeground);
        Assert.AreEqual(0, fixture.Platform.RequestProcessTerminationCalls);
    }

    [TestMethod]
    public async Task CleanupAndRecoveryFailureFailClosedWithBackgroundActive()
    {
        using var fixture = CreateFixture(backgroundEnabled: true);
        await using var host = fixture.Host;
        var client = await host.AttachInteractiveClientAsync();
        fixture.Runtime.InteractiveSessionDisposeFailure = new IOException("interactive cleanup failed");
        fixture.Runtime.StopFailure = new IOException("runtime recovery failed");

        await Assert.ThrowsAsync<Exception>(async () => await client.DisposeAsync());
        var state = await host.GetBackgroundStateAsync();

        Assert.IsTrue(host.Snapshot.IsRuntimeUnsafe);
        Assert.IsFalse(host.Snapshot.AcceptsInteractiveAttachments);
        Assert.IsFalse(host.Snapshot.HasBackgroundLease);
        Assert.IsFalse(host.Snapshot.HasRuntimeComposition);
        Assert.AreEqual(BackendLifetimeReason.None, fixture.Coordinator.ActiveReasons);
        Assert.AreEqual(AndroidBackgroundSyncFailureKind.RuntimeUnsafe, state.FailureKind);
        Assert.IsTrue(state.RequiresProcessRestart);
        Assert.IsTrue(fixture.Settings.Current.IsEnabled);
        Assert.IsFalse(fixture.Platform.IsForeground);
        Assert.IsFalse(fixture.Platform.IsServiceStarted);
        Assert.AreEqual(1, fixture.Platform.RequestProcessTerminationCalls);
    }

    [TestMethod]
    public async Task UnsafeCleanupDuringReplacementRejectsReplacementAttachment()
    {
        using var fixture = CreateFixture(backgroundEnabled: true);
        await using var host = fixture.Host;
        _ = await host.AttachInteractiveClientAsync();
        fixture.Runtime.InteractiveSessionDisposeFailure = new IOException("interactive cleanup failed");
        fixture.Runtime.StopFailure = new IOException("runtime recovery failed");

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => host.AttachInteractiveClientAsync());

        Assert.IsTrue(host.Snapshot.IsRuntimeUnsafe);
        Assert.IsFalse(host.Snapshot.HasInteractiveAttachment);
        Assert.AreEqual(1, fixture.Factory.CreateCalls);
    }

    [TestMethod]
    public async Task UnsafeCleanupWhileServiceStopsDisposesRuntimeExactlyOnce()
    {
        using var fixture = CreateFixture(backgroundEnabled: true);
        var host = fixture.Host;
        var client = await host.AttachInteractiveClientAsync();
        var disposeEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var disposeRelease = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        fixture.Runtime.InteractiveSessionDisposeEntered = disposeEntered;
        fixture.Runtime.InteractiveSessionDisposeRelease = disposeRelease.Task;
        fixture.Runtime.InteractiveSessionDisposeFailure = new IOException("interactive cleanup failed");
        fixture.Runtime.StopFailure = new IOException("runtime recovery failed");

        var clientDisposeTask = client.DisposeAsync().AsTask();
        await disposeEntered.Task;
        var hostDisposeTask = host.DisposeAsync().AsTask();
        disposeRelease.TrySetResult();
        try
        {
            await clientDisposeTask;
        }
        catch
        {
        }
        await hostDisposeTask;

        Assert.AreEqual(1, fixture.Runtime.DisposeCalls);
        Assert.AreEqual(1, fixture.Platform.RequestProcessTerminationCalls);
        Assert.IsTrue(host.Snapshot.IsDisposed);
    }

    [TestMethod]
    public async Task SuppressedAttachmentCleanupExceptionStillLeavesHostFailClosed()
    {
        using var fixture = CreateFixture(backgroundEnabled: true);
        await using var host = fixture.Host;
        var client = await host.AttachInteractiveClientAsync();
        fixture.Runtime.InteractiveSessionDisposeFailure = new IOException("interactive cleanup failed");
        fixture.Runtime.StopFailure = new IOException("runtime recovery failed");

        try
        {
            await client.DisposeAsync();
        }
        catch
        {
        }

        Assert.IsTrue(host.Snapshot.IsRuntimeUnsafe);
        Assert.IsFalse(host.Snapshot.HasBackgroundLease);
        Assert.AreEqual(1, fixture.Platform.RequestProcessTerminationCalls);
    }

    [TestMethod]
    public async Task UnsafeProcessDoesNotRecreateRuntimeButFreshProcessCanRestoreSetting()
    {
        using var fixture = CreateFixture(backgroundEnabled: true);
        await using var host = fixture.Host;
        var client = await host.AttachInteractiveClientAsync();
        fixture.Runtime.InteractiveSessionDisposeFailure = new IOException("interactive cleanup failed");
        fixture.Runtime.StopFailure = new IOException("runtime recovery failed");
        try
        {
            await client.DisposeAsync();
        }
        catch
        {
        }

        await host.RestoreBackgroundStateAsync();
        Assert.AreEqual(1, fixture.Factory.CreateCalls);
        Assert.IsTrue(fixture.Settings.Current.IsEnabled);

        using var freshFixture = CreateFixture(backgroundEnabled: fixture.Settings.Current.IsEnabled);
        await using var freshHost = freshFixture.Host;
        await freshHost.RestoreBackgroundStateAsync();

        Assert.AreEqual(1, freshFixture.Factory.CreateCalls);
        Assert.IsTrue(freshHost.Snapshot.HasBackgroundLease);
        Assert.IsFalse(freshHost.Snapshot.IsRuntimeUnsafe);
    }

    [TestMethod]
    public async Task UnsafeCleanupDuringDatabaseResetDisposesAttachmentAndClosesAdmission()
    {
        using var fixture = CreateFixture(backgroundEnabled: true);
        await using var host = fixture.Host;
        var client = await host.AttachInteractiveClientAsync();
        fixture.Runtime.InteractiveSessionDisposeFailure = new IOException("interactive cleanup failed");
        fixture.Runtime.StopFailure = new IOException("runtime recovery failed");

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => client.ResetDatabaseAndRestartAsync());
        await client.DisposeAsync();

        Assert.AreEqual(AndroidInteractiveAttachmentState.Disposed, client.AttachmentState);
        Assert.IsTrue(host.Snapshot.IsRuntimeUnsafe);
        Assert.IsFalse(host.Snapshot.AcceptsInteractiveAttachments);
        Assert.AreEqual(1, fixture.Platform.RequestProcessTerminationCalls);
    }

    [TestMethod]
    public async Task BackgroundDisableOverlappingUnsafeCleanupHasNoEffectsAfterAdmissionCloses()
    {
        using var fixture = CreateFixture(backgroundEnabled: true);
        await using var host = fixture.Host;
        var client = await host.AttachInteractiveClientAsync();
        var disposeEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var disposeRelease = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        fixture.Runtime.InteractiveSessionDisposeEntered = disposeEntered;
        fixture.Runtime.InteractiveSessionDisposeRelease = disposeRelease.Task;
        fixture.Runtime.InteractiveSessionDisposeFailure = new IOException("interactive cleanup failed");
        fixture.Runtime.StopFailure = new IOException("runtime recovery failed");
        var writeCalls = fixture.Settings.WriteCalls;

        var disposeTask = client.DisposeAsync().AsTask();
        await disposeEntered.Task;
        var disableTask = host.SetBackgroundEnabledFromAttachmentAsync(client, false);
        disposeRelease.TrySetResult();
        try
        {
            await disposeTask;
        }
        catch
        {
        }

        var state = await disableTask;
        Assert.AreEqual(AndroidBackgroundSyncFailureKind.RuntimeUnsafe, state.FailureKind);
        Assert.AreEqual(writeCalls, fixture.Settings.WriteCalls);
        Assert.IsTrue(fixture.Settings.Current.IsEnabled);
        Assert.IsFalse(host.Snapshot.HasBackgroundLease);
        Assert.AreEqual(1, fixture.Platform.RequestProcessTerminationCalls);
    }

    [TestMethod]
    public async Task RepeatedAttachmentDisposalReleasesInteractiveOwnershipExactlyOnce()
    {
        using var fixture = CreateFixture(backgroundEnabled: false);
        await using var host = fixture.Host;
        var client = await host.AttachInteractiveClientAsync();

        await client.DisposeAsync();
        await client.DisposeAsync();

        Assert.AreEqual(1, fixture.Runtime.ClosedInteractiveSessionCalls);
        Assert.AreEqual(1, fixture.Runtime.StopCalls);
        Assert.AreEqual(1, fixture.Runtime.DisposeCalls);
        Assert.AreEqual(BackendLifetimeReason.None, fixture.Coordinator.ActiveReasons);
    }

    [TestMethod]
    public async Task SupersededEndpointProxyRejectsEveryLaterInvocation()
    {
        using var fixture = CreateFixture(backgroundEnabled: false);
        await using var host = fixture.Host;
        var first = await host.AttachInteractiveClientAsync();
        var staleEndpoints = await first.GetEndpointsAsync();

        var second = await host.AttachInteractiveClientAsync();

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => staleEndpoints.GetLocalDeviceInfoAsync());
        await Assert.ThrowsAsync<InvalidOperationException>(
            () => staleEndpoints.StartDeviceEnrollmentAsync());
        await Assert.ThrowsAsync<InvalidOperationException>(
            () => staleEndpoints.AddDeviceByCodeAsync(Guid.NewGuid(), "stale-code"));
        Assert.IsInstanceOfType<AndroidAttachmentAuthorizedEndpoints>(
            await second.GetEndpointsAsync());
        await first.DisposeAsync();
        await second.DisposeAsync();
    }

    [TestMethod]
    public async Task LateOldDetachCannotReleaseNewInteractiveAuthority()
    {
        using var fixture = CreateFixture(backgroundEnabled: false);
        await using var host = fixture.Host;
        var first = await host.AttachInteractiveClientAsync();
        var second = await host.AttachInteractiveClientAsync();

        await first.DisposeAsync();

        Assert.IsTrue(host.Snapshot.HasInteractiveAttachment);
        Assert.AreEqual(BackendLifetimeReason.InteractiveUi, fixture.Coordinator.ActiveReasons);
        Assert.IsInstanceOfType<AndroidAttachmentAuthorizedEndpoints>(
            await second.GetEndpointsAsync());
        await second.DisposeAsync();
    }

    [TestMethod]
    public async Task ForegroundEntryRejectionResultPreservesDesiredSettingWithoutCreatingRuntime()
    {
        using var fixture = CreateFixture(backgroundEnabled: false);
        fixture.Platform.ForegroundEntryResult = new AndroidForegroundEntryResult(
            IsForegroundEntered: false,
            AndroidNotificationAvailability.ChannelDisabled,
            AndroidForegroundEntryFailureKind.ForegroundStartRejected,
            RequiresUserAction: true,
            "Foreground start rejected.");
        await using var host = fixture.Host;

        var state = await host.SetBackgroundEnabledFromServiceAsync(true);

        Assert.IsTrue(state.IsEnabled);
        Assert.IsTrue(fixture.Settings.Current.IsEnabled);
        Assert.IsFalse(state.IsOperational);
        Assert.IsTrue(state.RequiresUserAction);
        Assert.AreEqual(AndroidBackgroundSyncFailureKind.ForegroundService, state.FailureKind);
        Assert.AreEqual(AndroidServiceStartPhase.ForegroundStartRejected, state.ServiceStartPhase);
        Assert.AreEqual(AndroidNotificationAvailability.ChannelDisabled, state.NotificationAvailability);
        Assert.AreEqual(0, fixture.Factory.CreateCalls);
        Assert.AreEqual(BackendLifetimeReason.None, fixture.Coordinator.ActiveReasons);
    }

    [TestMethod]
    public async Task RuntimeUnsafeRejectsEndpointProxyAfterCleanup()
    {
        using var fixture = CreateFixture(backgroundEnabled: true);
        await using var host = fixture.Host;
        var client = await host.AttachInteractiveClientAsync();
        var endpoints = await client.GetEndpointsAsync();
        fixture.Runtime.InteractiveSessionDisposeFailure = new IOException("interactive cleanup failed");
        fixture.Runtime.StopFailure = new IOException("runtime recovery failed");

        try
        {
            await client.DisposeAsync();
        }
        catch
        {
        }

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => endpoints.GetLocalDeviceInfoAsync());
        Assert.IsTrue(host.Snapshot.IsRuntimeUnsafe);
        Assert.IsFalse(host.Snapshot.AcceptsInteractiveAttachments);
    }

    private static AndroidRuntimeServiceHostFixture CreateFixture(
        bool backgroundEnabled,
        bool secureStorageAvailable = true)
    {
        var backendTestHost = new BackendTestHost();
        var endpoints = backendTestHost.Services.GetRequiredService<IEndpoints>();
        var runtime = new FakeBackendRuntime(endpoints);
        var coordinator = new BackendRuntimeLifetimeCoordinator(runtime);
        var composition = new BackendRuntimeComposition(runtime, coordinator, "test-data");
        var factory = new FakeAndroidRuntimeCompositionFactory(() => composition);
        var settings = new FakeBackgroundSyncSettingsStore(backgroundEnabled);
        var platform = new FakeAndroidForegroundServiceController();
        var secureStorage = new FakeAndroidSecureStorageAvailability
        {
            IsAvailable = secureStorageAvailable
        };
        var host = new AndroidRuntimeServiceHost(factory, settings, platform, secureStorage);
        return new AndroidRuntimeServiceHostFixture(
            backendTestHost,
            host,
            runtime,
            coordinator,
            factory,
            settings,
            platform,
            secureStorage,
            endpoints);
    }
}
