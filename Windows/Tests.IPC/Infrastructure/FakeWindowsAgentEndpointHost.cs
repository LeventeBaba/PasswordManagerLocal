using PasswordManagerLocal.Windows.Agent.Endpoint;

namespace PasswordManagerLocal.Windows.Tests.IPC.Infrastructure;

internal sealed class FakeWindowsAgentEndpointHost : IWindowsAgentEndpointHost
{
    private readonly TaskCompletionSource _completion = new(
        TaskCreationOptions.RunContinuationsAsynchronously);

    public WindowsAgentEndpointHostSnapshot Snapshot { get; set; } = new(
        WindowsAgentEndpointHostState.Stopped,
        Failure: null,
        DateTimeOffset.UtcNow);
    public Task Completion => _completion.Task;
    public Exception? StartFailure { get; set; }
    public Exception? StopFailure { get; set; }
    public Exception? DisposeFailure { get; set; }
    public TaskCompletionSource? StopRelease { get; set; }
    public int StartCount { get; private set; }
    public int StopCount { get; private set; }
    public int DisposeCount { get; private set; }
    public ICollection<string>? OperationLog { get; set; }

    public event EventHandler? StateChanged;

    public Task StartAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        StartCount++;
        OperationLog?.Add("endpoint-start");
        if (StartFailure is not null)
            return Task.FromException(StartFailure);
        Snapshot = new WindowsAgentEndpointHostSnapshot(
            WindowsAgentEndpointHostState.Ready,
            null,
            DateTimeOffset.UtcNow);
        StateChanged?.Invoke(this, EventArgs.Empty);
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        StopCount++;
        OperationLog?.Add("endpoint-stop");
        if (StopFailure is not null)
            return Task.FromException(StopFailure);
        return StopCoreAsync(cancellationToken);
    }

    private async Task StopCoreAsync(CancellationToken cancellationToken)
    {
        if (StopRelease is not null)
            await StopRelease.Task.WaitAsync(cancellationToken);
        Snapshot = new WindowsAgentEndpointHostSnapshot(
            WindowsAgentEndpointHostState.Stopped,
            null,
            DateTimeOffset.UtcNow);
        StateChanged?.Invoke(this, EventArgs.Empty);
        _completion.TrySetResult();
    }

    public void Fail(Exception failure)
    {
        Snapshot = new WindowsAgentEndpointHostSnapshot(
            WindowsAgentEndpointHostState.Failed,
            failure,
            DateTimeOffset.UtcNow);
        StateChanged?.Invoke(this, EventArgs.Empty);
        _completion.TrySetException(failure);
    }

    public ValueTask DisposeAsync()
    {
        DisposeCount++;
        OperationLog?.Add("endpoint-dispose");
        _completion.TrySetResult();
        return DisposeFailure is null
            ? ValueTask.CompletedTask
            : ValueTask.FromException(DisposeFailure);
    }
}
