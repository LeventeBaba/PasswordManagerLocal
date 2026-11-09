namespace PasswordManagerLocal.Windows.Ipc.Coordination.ProcessLocks;

public sealed class ProcessInstanceAlreadyOwnedException : InvalidOperationException
{
    public ProcessInstanceAlreadyOwnedException(string lockFilePath)
        : base($"The process instance lock '{lockFilePath}' is already held.")
    {
        LockFilePath = lockFilePath;
    }

    public string LockFilePath { get; }
}
