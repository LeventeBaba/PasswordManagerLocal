namespace PasswordManagerLocal.Windows.Ipc.Constants.Coordination;

public static class ProcessLockConstants
{
    public const int FileProcessInstanceLockProbeErrorFileNotFound = 2;
    public const int FileProcessInstanceLockProbeErrorPathNotFound = 3;
    public const int FileProcessInstanceLockProbeErrorSharingViolation = 32;
    public const int FileProcessInstanceLockProbeErrorLockViolation = 33;
    public const uint FileProcessInstanceLockProbeGenericRead = 0x80000000;
    public const uint FileProcessInstanceLockProbeFileAttributeNormal = 0x80;
    public const int ProcessInstanceLockFileOpenerErrorSharingViolation = 32;
    public const int ProcessInstanceLockFileOpenerErrorLockViolation = 33;
}
