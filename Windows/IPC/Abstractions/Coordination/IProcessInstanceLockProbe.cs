namespace PasswordManagerLocal.Windows.Ipc.Abstractions.Coordination;

public interface IProcessInstanceLockProbe
{
    ProcessInstanceLockProbeResult Probe();
}