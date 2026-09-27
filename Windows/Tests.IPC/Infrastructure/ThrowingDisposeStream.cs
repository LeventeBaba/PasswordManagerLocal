namespace PasswordManagerLocal.Windows.Tests.IPC.Infrastructure;

internal sealed class ThrowingDisposeStream : MemoryStream
{
    private readonly Exception _disposeException;
    private int _disposeCallCount;

    public ThrowingDisposeStream(Exception disposeException)
    {
        _disposeException = disposeException
            ?? throw new ArgumentNullException(nameof(disposeException));
    }

    public int DisposeCallCount => Volatile.Read(ref _disposeCallCount);

    protected override void Dispose(bool disposing)
    {
        Interlocked.Increment(ref _disposeCallCount);
        throw _disposeException;
    }

    public override ValueTask DisposeAsync()
    {
        Interlocked.Increment(ref _disposeCallCount);
        return ValueTask.FromException(_disposeException);
    }
}
