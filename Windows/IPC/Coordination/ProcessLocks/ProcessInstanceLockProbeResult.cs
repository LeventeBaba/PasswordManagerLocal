namespace PasswordManagerLocal.Windows.Ipc.Coordination.ProcessLocks;

public enum ProcessInstanceLockProbeResult
{
    Free = 0,
    Held = 1,
    Uncertain = 2
}