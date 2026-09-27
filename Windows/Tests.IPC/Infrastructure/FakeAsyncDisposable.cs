namespace PasswordManagerLocal.Windows.Tests.IPC.Infrastructure;

internal sealed class FakeAsyncDisposable : IAsyncDisposable
{
    public ICollection<string>? OperationLog { get; set; }
    public string OperationName { get; set; } = "async-dispose";
    public Task? DisposeTaskOverride { get; set; }
    public Exception? DisposeFailure { get; set; }
    public ManualResetEventSlim? DisposeEntered { get; set; }
    public ManualResetEventSlim? DisposeBlocker { get; set; }
    public int DisposeCount { get; private set; }

    public ValueTask DisposeAsync()
    {
        DisposeCount++;
        OperationLog?.Add(OperationName);
        DisposeEntered?.Set();
        DisposeBlocker?.Wait();
        if (DisposeTaskOverride is not null)
            return new ValueTask(DisposeTaskOverride);
        return DisposeFailure is null
            ? ValueTask.CompletedTask
            : ValueTask.FromException(DisposeFailure);
    }
}
