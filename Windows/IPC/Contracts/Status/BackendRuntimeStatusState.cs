namespace PasswordManagerLocal.Windows.Ipc.Contracts.Status;

public enum BackendRuntimeStatusState
{
    NotStarted = 0,
    Starting = 1,
    Ready = 2,
    WaitingForDeviceUnlock = 3,
    Failed = 4,
    Stopping = 5,
    Stopped = 6,
    Unavailable = 7
}
