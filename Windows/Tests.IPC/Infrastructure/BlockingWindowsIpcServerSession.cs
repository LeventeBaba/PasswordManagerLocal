using PasswordManagerLocal.Windows.Ipc.Server;

namespace PasswordManagerLocal.Windows.Tests.IPC.Infrastructure;

internal sealed class BlockingWindowsIpcServerSession : IWindowsIpcServerSession
{
    private readonly TaskCompletionSource _release = new(
        TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource _disposeRelease = new(
        TaskCreationOptions.RunContinuationsAsynchronously);

    public int RunCount { get; private set; }
    public int DisposeCount { get; private set; }
    public bool FailRun { get; set; }
    public bool BlockDispose { get; set; }

    public async Task RunAsync(CancellationToken cancellationToken = default)
    {
        RunCount++;
        if (FailRun)
            throw new IOException("session failed");

        using var registration = cancellationToken.Register(() => _release.TrySetResult());
        await _release.Task;
    }

    public async ValueTask DisposeAsync()
    {
        DisposeCount++;
        _release.TrySetResult();
        if (BlockDispose)
            await _disposeRelease.Task;
    }

    public void Complete() => _release.TrySetResult();

    public void CompleteDispose() => _disposeRelease.TrySetResult();
}
