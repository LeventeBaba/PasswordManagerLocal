namespace PasswordManagerLocal.Android.Runtime;

public sealed class AndroidServiceStartStateMachine
{
    private readonly object _gate = new();
    private AndroidServiceStartStatus _status = new(
        AndroidServiceStartPhase.Stopped,
        IsForegroundEntered: false,
        IsRuntimeReady: false,
        AndroidNotificationAvailability.Unknown,
        RequiresUserAction: false,
        RequiresProcessRestart: false,
        SafeMessage: null);

    public AndroidServiceStartStatus Status
    {
        get
        {
            lock (_gate)
                return _status;
        }
    }

    public void RecordServiceRequested()
    {
        lock (_gate)
        {
            if (_status.RequiresProcessRestart)
                return;

            _status = _status with
            {
                Phase = AndroidServiceStartPhase.ServiceRequested,
                IsRuntimeReady = false,
                RequiresUserAction = false,
                SafeMessage = null
            };
        }
    }

    public void RecordServiceStarting()
    {
        lock (_gate)
        {
            if (_status.RequiresProcessRestart)
                return;

            _status = _status with
            {
                Phase = AndroidServiceStartPhase.ServiceStarting,
                IsRuntimeReady = false,
                RequiresUserAction = false,
                SafeMessage = null
            };
        }
    }

    public void RecordForegroundEntry(AndroidForegroundEntryResult result)
    {
        ArgumentNullException.ThrowIfNull(result);
        lock (_gate)
        {
            if (_status.RequiresProcessRestart)
                return;

            _status = new AndroidServiceStartStatus(
                result.ServiceStartPhase,
                result.IsForegroundEntered,
                IsRuntimeReady: false,
                result.NotificationAvailability,
                result.RequiresUserAction,
                RequiresProcessRestart: false,
                result.SafeMessage);
        }
    }

    public void RecordRuntimeState(AndroidBackgroundSyncServiceState state)
    {
        ArgumentNullException.ThrowIfNull(state);
        lock (_gate)
        {
            if (_status.RequiresProcessRestart && !state.RequiresProcessRestart)
                return;

            _status = new AndroidServiceStartStatus(
                state.ServiceStartPhase,
                state.IsForegroundActive,
                state.IsRuntimeReady,
                state.NotificationAvailability,
                state.RequiresUserAction,
                state.RequiresProcessRestart,
                state.SafeMessage);
        }
    }

    public void RecordRuntimeUnsafe(string safeMessage)
    {
        lock (_gate)
        {
            _status = new AndroidServiceStartStatus(
                AndroidServiceStartPhase.RuntimeUnsafe,
                IsForegroundEntered: false,
                IsRuntimeReady: false,
                AndroidNotificationAvailability.Unknown,
                RequiresUserAction: true,
                RequiresProcessRestart: true,
                safeMessage);
        }
    }

    public void RecordStopped()
    {
        lock (_gate)
        {
            if (_status.RequiresProcessRestart)
                return;

            _status = new AndroidServiceStartStatus(
                AndroidServiceStartPhase.Stopped,
                IsForegroundEntered: false,
                IsRuntimeReady: false,
                AndroidNotificationAvailability.Unknown,
                RequiresUserAction: false,
                RequiresProcessRestart: false,
                SafeMessage: null);
        }
    }
}
