using PasswordManagerLocal.Windows.Ipc.Coordination;

namespace PasswordManagerLocal.Windows.Tests.Host;

internal sealed class RetainingThrowingLockFileOpener : IProcessInstanceLockFileOpener
{
    public Stream? OpenExclusive(string lockFilePath)
    {
        try
        {
            return new RetainingThrowingStream(new FileStream(
                lockFilePath,
                FileMode.OpenOrCreate,
                FileAccess.ReadWrite,
                FileShare.None,
                bufferSize: 1,
                FileOptions.None));
        }
        catch (IOException exception) when ((exception.HResult & 0xFFFF) is 32 or 33)
        {
            return null;
        }
    }
}
