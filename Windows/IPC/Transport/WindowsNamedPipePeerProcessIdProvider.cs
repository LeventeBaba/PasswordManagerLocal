using Microsoft.Win32.SafeHandles;
using System.Runtime.InteropServices;

namespace PasswordManagerLocal.Windows.Ipc.Transport;

internal sealed class WindowsNamedPipePeerProcessIdProvider
{
    public int GetClientProcessId(SafePipeHandle pipeHandle)
    {
        ArgumentNullException.ThrowIfNull(pipeHandle);
        if (!GetNamedPipeClientProcessId(pipeHandle, out var processId) || processId == 0)
            throw new InvalidOperationException("The named-pipe client process could not be verified.");
        return checked((int)processId);
    }

    public int GetServerProcessId(SafePipeHandle pipeHandle)
    {
        ArgumentNullException.ThrowIfNull(pipeHandle);
        if (!GetNamedPipeServerProcessId(pipeHandle, out var processId) || processId == 0)
            throw new InvalidOperationException("The named-pipe server process could not be verified.");
        return checked((int)processId);
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetNamedPipeClientProcessId(
        SafePipeHandle pipe,
        out uint clientProcessId);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetNamedPipeServerProcessId(
        SafePipeHandle pipe,
        out uint serverProcessId);
}
