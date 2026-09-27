using PasswordManagerLocal.Windows.Ipc.Server;

namespace PasswordManagerLocal.Windows.Tests.IPC.Infrastructure;

internal sealed class FakeWindowsIpcServerHost : IWindowsIpcServerHost
{
    private readonly TaskCompletionSource _completion = new(
        TaskCreationOptions.RunContinuationsAsynchronously);

    public int ActiveSessionCount { get; set; }
    public Exception? ListenerFailure { get; private set; }
    public Task Completion => _completion.Task;
    public int StartCount { get; private set; }
    public int CloseActiveSessionsCount { get; private set; }
    public int StopCount { get; private set; }
    public int DisposeCount { get; private set; }
    public bool ThrowOnStart { get; set; }
    public Exception? StartFailure { get; set; }
    public Exception? CloseActiveSessionsFailure { get; set; }
    public Exception? StopFailure { get; set; }
    public Task? StopTaskOverride { get; set; }
    public Exception? DisposeFailure { get; set; }
    public Task? DisposeTaskOverride { get; set; }
    public ICollection<string>? OperationLog { get; set; }

    public Task StartAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        StartCount++;
        OperationLog?.Add("control-start");
        if (StartFailure is not null)
            return Task.FromException(StartFailure);
        if (ThrowOnStart)
            throw new IOException("listener start failed");
        return Task.CompletedTask;
    }

    public Task CloseActiveSessionsAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        CloseActiveSessionsCount++;
        OperationLog?.Add("control-close-sessions");
        ActiveSessionCount = 0;
        return CloseActiveSessionsFailure is null
            ? Task.CompletedTask
            : Task.FromException(CloseActiveSessionsFailure);
    }

    public Task StopAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        StopCount++;
        OperationLog?.Add("control-stop");
        if (StopTaskOverride is not null)
            return StopTaskOverride;
        _completion.TrySetResult();
        return StopFailure is null
            ? Task.CompletedTask
            : Task.FromException(StopFailure);
    }

    public ValueTask DisposeAsync()
    {
        DisposeCount++;
        OperationLog?.Add("control-dispose");
        if (DisposeTaskOverride is not null)
            return new ValueTask(DisposeTaskOverride);
        _completion.TrySetResult();
        return DisposeFailure is null
            ? ValueTask.CompletedTask
            : ValueTask.FromException(DisposeFailure);
    }

    public void FailListener(Exception exception)
    {
        ListenerFailure = exception;
        _completion.TrySetException(exception);
    }
}
