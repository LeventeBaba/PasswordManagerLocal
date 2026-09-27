namespace PasswordManagerLocal.Windows.Ipc.Coordination;

public interface IProcessInstanceLockFileOpener
{
    Stream? OpenExclusive(string lockFilePath);
}
