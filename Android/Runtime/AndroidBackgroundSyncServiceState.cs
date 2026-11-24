using PasswordManagerLocal.Common.Contracts.BackgroundSync;

namespace PasswordManagerLocal.Android.Runtime;

public sealed record AndroidBackgroundSyncServiceState(
    bool IsEnabled,
    bool IsAvailable,
    bool IsDegraded,
    bool IsTransitionInProgress,
    AndroidBackgroundSyncFailureKind FailureKind,
    string? SafeMessage,
    bool IsBackgroundLeaseActive,
    bool IsForegroundActive,
    bool IsSecureStorageDeferred,
    bool RequiresProcessRestart,
    AndroidServiceStartPhase ServiceStartPhase,
    bool IsRuntimeReady,
    bool RequiresUserAction,
    AndroidNotificationAvailability NotificationAvailability,
    BackgroundSyncOperationalState OperationalState = BackgroundSyncOperationalState.Unspecified)
{
    public bool IsOperational =>
        IsEnabled &&
        (OperationalState == BackgroundSyncOperationalState.EnabledIdle ||
        (IsForegroundActive &&
        IsBackgroundLeaseActive &&
        IsRuntimeReady &&
        !IsSecureStorageDeferred &&
        !RequiresProcessRestart &&
        !RequiresUserAction &&
        FailureKind == AndroidBackgroundSyncFailureKind.None));
}
