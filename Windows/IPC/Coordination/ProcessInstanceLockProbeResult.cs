namespace PasswordManagerLocal.Windows.Ipc.Coordination;

public enum ProcessInstanceLockProbeResult
{
    Free = 0,
    Held = 1,
    Uncertain = 2
}