namespace PasswordManagerLocal.Windows.Ipc.Coordination;

public sealed class ProcessInstanceLockFileOpener : IProcessInstanceLockFileOpener
{
    private const int ErrorSharingViolation = 32;
    private const int ErrorLockViolation = 33;

    public Stream? OpenExclusive(string lockFilePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(lockFilePath);

        try
        {
            return new FileStream(
                lockFilePath,
                FileMode.OpenOrCreate,
                FileAccess.ReadWrite,
                FileShare.None,
                bufferSize: 1,
                FileOptions.None);
        }
        catch (IOException exception) when (IsOwnershipUnavailable(exception))
        {
            return null;
        }
    }

    private static bool IsOwnershipUnavailable(IOException exception)
    {
        var nativeErrorCode = exception.HResult & 0xFFFF;
        return nativeErrorCode is ErrorSharingViolation or ErrorLockViolation;
    }
}
