using System.Diagnostics;

namespace PasswordManagerLocal.Windows.Frontend.AgentConnection;

public sealed class WindowsAgentProcessExitWaiter : IWindowsAgentProcessExitWaiter
{
    public async Task<bool> WaitForExitAsync(
        int processId,
        TimeSpan timeout,
        CancellationToken cancellationToken = default)
    {
        if (processId <= 0)
            throw new ArgumentOutOfRangeException(nameof(processId));
        if (timeout <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(timeout));

        Process process;
        try
        {
            process = Process.GetProcessById(processId);
        }
        catch (ArgumentException)
        {
            return true;
        }

        using (process)
        using (var timeoutSource = new CancellationTokenSource(timeout))
        using (var linkedSource = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken,
            timeoutSource.Token))
        {
            try
            {
                if (process.HasExited)
                    return true;
                await process.WaitForExitAsync(linkedSource.Token);
                return true;
            }
            catch (InvalidOperationException)
            {
                return true;
            }
            catch (OperationCanceledException) when (
                timeoutSource.IsCancellationRequested &&
                !cancellationToken.IsCancellationRequested)
            {
                return false;
            }
        }
    }
}
