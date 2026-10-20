namespace PasswordManagerLocal.Windows.Ipc.Abstractions.Coordination;

public interface IProcessInstanceLockFileOpener
{
    Stream? OpenExclusive(string lockFilePath);
}
