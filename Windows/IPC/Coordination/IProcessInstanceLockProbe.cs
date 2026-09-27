namespace PasswordManagerLocal.Windows.Ipc.Coordination;

public interface IProcessInstanceLockProbe
{
    ProcessInstanceLockProbeResult Probe();
}