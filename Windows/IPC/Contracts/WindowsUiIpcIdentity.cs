namespace PasswordManagerLocal.Windows.Ipc.Contracts;

public sealed record WindowsUiIpcIdentity
{
    public WindowsUiIpcIdentity(
        int processId,
        int windowsSessionId,
        Guid instanceId)
    {
        if (processId <= 0)
            throw new ArgumentOutOfRangeException(nameof(processId));
        if (windowsSessionId < 0)
            throw new ArgumentOutOfRangeException(nameof(windowsSessionId));
        if (instanceId == Guid.Empty)
            throw new ArgumentException("The UI instance ID cannot be empty.", nameof(instanceId));

        ProcessId = processId;
        WindowsSessionId = windowsSessionId;
        InstanceId = instanceId;
    }

    public int ProcessId { get; }
    public int WindowsSessionId { get; }
    public Guid InstanceId { get; }
}
