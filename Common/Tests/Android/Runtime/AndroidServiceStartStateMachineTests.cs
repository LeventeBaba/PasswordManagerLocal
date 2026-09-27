using Microsoft.VisualStudio.TestTools.UnitTesting;
using PasswordManagerLocal.Android.Runtime;

namespace PasswordManagerLocal.Common.Tests.Android.Runtime;

[TestClass]
public sealed class AndroidServiceStartStateMachineTests
{
    [TestMethod]
    public void ForegroundRequestIsNotRuntimeReady()
    {
        var stateMachine = new AndroidServiceStartStateMachine();

        stateMachine.RecordServiceRequested();
        stateMachine.RecordServiceStarting();
        stateMachine.RecordForegroundEntry(new AndroidForegroundEntryResult(
            IsForegroundEntered: true,
            AndroidNotificationAvailability.Available,
            AndroidForegroundEntryFailureKind.None,
            RequiresUserAction: false,
            SafeMessage: null));

        Assert.AreEqual(AndroidServiceStartPhase.ForegroundEntered, stateMachine.Status.Phase);
        Assert.IsTrue(stateMachine.Status.IsForegroundEntered);
        Assert.IsFalse(stateMachine.Status.IsRuntimeReady);
    }

    [TestMethod]
    public void NotificationUnavailableIsTruthfulAndRequiresUserAction()
    {
        var stateMachine = new AndroidServiceStartStateMachine();

        stateMachine.RecordForegroundEntry(new AndroidForegroundEntryResult(
            IsForegroundEntered: true,
            AndroidNotificationAvailability.PermissionDenied,
            AndroidForegroundEntryFailureKind.None,
            RequiresUserAction: true,
            "Notification permission is denied."));

        Assert.AreEqual(AndroidServiceStartPhase.NotificationUnavailable, stateMachine.Status.Phase);
        Assert.IsFalse(stateMachine.Status.IsRuntimeReady);
        Assert.IsTrue(stateMachine.Status.RequiresUserAction);
    }

    [TestMethod]
    public void ForegroundRejectionNeverReportsOperational()
    {
        var stateMachine = new AndroidServiceStartStateMachine();

        stateMachine.RecordForegroundEntry(new AndroidForegroundEntryResult(
            IsForegroundEntered: false,
            AndroidNotificationAvailability.Unknown,
            AndroidForegroundEntryFailureKind.ForegroundStartRejected,
            RequiresUserAction: true,
            "Foreground start rejected."));

        Assert.AreEqual(AndroidServiceStartPhase.ForegroundStartRejected, stateMachine.Status.Phase);
        Assert.IsFalse(stateMachine.Status.IsForegroundEntered);
        Assert.IsFalse(stateMachine.Status.IsRuntimeReady);
    }

    [TestMethod]
    public void UnsafeStateCannotBeOverwrittenByLaterStartCallbacks()
    {
        var stateMachine = new AndroidServiceStartStateMachine();
        stateMachine.RecordRuntimeUnsafe("Fresh process required.");

        stateMachine.RecordServiceRequested();
        stateMachine.RecordServiceStarting();
        stateMachine.RecordStopped();

        Assert.AreEqual(AndroidServiceStartPhase.RuntimeUnsafe, stateMachine.Status.Phase);
        Assert.IsTrue(stateMachine.Status.RequiresProcessRestart);
    }
    [TestMethod]
    public void LateSuccessfulRuntimeCallbackCannotOverwriteUnsafeState()
    {
        var stateMachine = new AndroidServiceStartStateMachine();
        stateMachine.RecordRuntimeUnsafe("Fresh process required.");

        stateMachine.RecordRuntimeState(new AndroidBackgroundSyncServiceState(
            IsEnabled: true,
            IsAvailable: true,
            IsDegraded: false,
            IsTransitionInProgress: false,
            AndroidBackgroundSyncFailureKind.None,
            SafeMessage: null,
            IsBackgroundLeaseActive: true,
            IsForegroundActive: true,
            IsSecureStorageDeferred: false,
            RequiresProcessRestart: false,
            AndroidServiceStartPhase.RuntimeReady,
            IsRuntimeReady: true,
            RequiresUserAction: false,
            AndroidNotificationAvailability.Available));

        Assert.AreEqual(AndroidServiceStartPhase.RuntimeUnsafe, stateMachine.Status.Phase);
        Assert.IsTrue(stateMachine.Status.RequiresProcessRestart);
        Assert.IsFalse(stateMachine.Status.IsRuntimeReady);
    }

    [TestMethod]
    public void NotificationConstructionFailureIsNotClassifiedAsForegroundRejection()
    {
        var result = new AndroidForegroundEntryResult(
            IsForegroundEntered: false,
            NotificationAvailability: AndroidNotificationAvailability.Unknown,
            FailureKind: AndroidForegroundEntryFailureKind.NotificationPostingFailed,
            RequiresUserAction: true,
            SafeMessage: "Notification unavailable.");
        var stateMachine = new AndroidServiceStartStateMachine();

        stateMachine.RecordForegroundEntry(result);

        Assert.AreEqual(AndroidServiceStartPhase.NotificationUnavailable, stateMachine.Status.Phase);
        Assert.IsFalse(stateMachine.Status.IsForegroundEntered);
        Assert.IsFalse(stateMachine.Status.IsRuntimeReady);
    }

}
