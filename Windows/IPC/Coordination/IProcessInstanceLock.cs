namespace PasswordManagerLocal.Windows.Ipc.Coordination;

public interface IProcessInstanceLock : IDisposable
{
    string LockFilePath { get; }
    bool IsOwner { get; }

    bool TryAcquire();
    void EnsureOwnership();
}
