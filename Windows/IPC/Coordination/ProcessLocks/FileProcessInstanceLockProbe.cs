using Microsoft.Win32.SafeHandles;
using System.Runtime.InteropServices;

namespace PasswordManagerLocal.Windows.Ipc.Coordination.ProcessLocks;

public sealed class FileProcessInstanceLockProbe : IProcessInstanceLockProbe
{
    private readonly string _lockFilePath;
    private readonly IProcessInstanceLockFileOpener? _fileOpener;

    public FileProcessInstanceLockProbe(
        string lockFilePath,
        IProcessInstanceLockFileOpener? fileOpener = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(lockFilePath);
        if (!Path.IsPathFullyQualified(lockFilePath))
            throw new ArgumentException("The process lock file path must be absolute.", nameof(lockFilePath));

        _lockFilePath = Path.GetFullPath(lockFilePath);
        _fileOpener = fileOpener;
    }

    public ProcessInstanceLockProbeResult Probe()
    {
        if (_fileOpener is not null)
            return ProbeWithInjectedOpener();

        try
        {
            using var handle = CreateFile(
                _lockFilePath,
                desiredAccess: ProcessLockConstants.FileProcessInstanceLockProbeGenericRead,
                FileShare.ReadWrite | FileShare.Delete,
                IntPtr.Zero,
                FileMode.Open,
                flagsAndAttributes: ProcessLockConstants.FileProcessInstanceLockProbeFileAttributeNormal,
                IntPtr.Zero);
            if (!handle.IsInvalid)
                return ProcessInstanceLockProbeResult.Free;

            var error = Marshal.GetLastWin32Error();
            if (error is ProcessLockConstants.FileProcessInstanceLockProbeErrorFileNotFound or ProcessLockConstants.FileProcessInstanceLockProbeErrorPathNotFound)
                return ProcessInstanceLockProbeResult.Free;
            return error is ProcessLockConstants.FileProcessInstanceLockProbeErrorSharingViolation or ProcessLockConstants.FileProcessInstanceLockProbeErrorLockViolation
                ? ProcessInstanceLockProbeResult.Held
                : ProcessInstanceLockProbeResult.Uncertain;
        }
        catch
        {
            return ProcessInstanceLockProbeResult.Uncertain;
        }
    }

    private ProcessInstanceLockProbeResult ProbeWithInjectedOpener()
    {
        try
        {
            using var handle = _fileOpener!.OpenExclusive(_lockFilePath);
            return handle is null
                ? ProcessInstanceLockProbeResult.Held
                : ProcessInstanceLockProbeResult.Free;
        }
        catch
        {
            return ProcessInstanceLockProbeResult.Uncertain;
        }
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeFileHandle CreateFile(
        string fileName,
        uint desiredAccess,
        FileShare shareMode,
        IntPtr securityAttributes,
        FileMode creationDisposition,
        uint flagsAndAttributes,
        IntPtr templateFile);
}
