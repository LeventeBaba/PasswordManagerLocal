namespace PasswordManagerLocal.Windows.Ipc.Abstractions.Coordination;

public interface IProcessInstanceLock : IDisposable
{
    string LockFilePath { get; }
    bool IsOwner { get; }

    bool TryAcquire();
    void EnsureOwnership();
}
