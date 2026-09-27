using PasswordManagerLocal.Windows.Frontend.AgentConnection;

namespace PasswordManagerLocal.Windows.Tests.IPC.Infrastructure;

internal sealed class FakeWindowsAgentProcessExitWaiter : IWindowsAgentProcessExitWaiter
{
    public TaskCompletionSource<bool>? Completion { get; set; }
    public bool Result { get; set; } = true;
    public int WaitCount { get; private set; }
    public int? ProcessId { get; private set; }
    public TimeSpan? Timeout { get; private set; }

    public Task<bool> WaitForExitAsync(
        int processId,
        TimeSpan timeout,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        WaitCount++;
        ProcessId = processId;
        Timeout = timeout;
        return Completion is null
            ? Task.FromResult(Result)
            : Completion.Task.WaitAsync(cancellationToken);
    }
}
