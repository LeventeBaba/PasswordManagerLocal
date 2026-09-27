using PasswordManagerLocal.Android.Runtime;

namespace PasswordManagerLocal.Common.Tests.Fakes;

public sealed class FakeAndroidForegroundServiceController : IAndroidForegroundServiceController
{
    public bool AreNotificationsEnabled
    {
        get => NotificationAvailability == AndroidNotificationAvailability.Available;
        set => NotificationAvailability = value
            ? AndroidNotificationAvailability.Available
            : AndroidNotificationAvailability.ApplicationDisabled;
    }

    public AndroidNotificationAvailability NotificationAvailability { get; set; } =
        AndroidNotificationAvailability.Available;
    public int EnsureServiceStartedCalls { get; private set; }
    public int EnterForegroundCalls { get; private set; }
    public int ExitForegroundCalls { get; private set; }
    public int RequestStopCalls { get; private set; }
    public int RequestProcessTerminationCalls { get; private set; }
    public bool IsForeground { get; private set; }
    public bool IsServiceStarted { get; private set; }
    public AndroidForegroundNotificationState? LastNotificationState { get; private set; }
    public Exception? EnterForegroundFailure { get; set; }
    public AndroidForegroundEntryResult? ForegroundEntryResult { get; set; }

    public void EnsureServiceStarted()
    {
        if (IsServiceStarted)
            return;

        EnsureServiceStartedCalls++;
        IsServiceStarted = true;
    }

    public AndroidForegroundEntryResult EnterForeground(AndroidForegroundNotificationState state)
    {
        EnterForegroundCalls++;
        LastNotificationState = state;
        if (EnterForegroundFailure is not null)
            throw EnterForegroundFailure;

        var result = ForegroundEntryResult ?? new AndroidForegroundEntryResult(
            IsForegroundEntered: true,
            NotificationAvailability,
            AndroidForegroundEntryFailureKind.None,
            RequiresUserAction: NotificationAvailability != AndroidNotificationAvailability.Available,
            SafeMessage: NotificationAvailability == AndroidNotificationAvailability.Available
                ? null
                : "The foreground notification is unavailable.");
        IsForeground = result.IsForegroundEntered;
        return result;
    }

    public void ExitForeground()
    {
        ExitForegroundCalls++;
        IsForeground = false;
    }

    public void RequestStop()
    {
        RequestStopCalls++;
        IsServiceStarted = false;
    }

    public void RequestProcessTermination()
    {
        RequestProcessTerminationCalls++;
    }
}
