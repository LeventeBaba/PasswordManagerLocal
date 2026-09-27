namespace PasswordManagerLocal.Windows.Tests.IPC.Infrastructure;

internal sealed class FirstWriteGateStream : Stream
{
    private readonly MemoryStream _bytes = new();
    private readonly object _gate = new();
    private readonly TaskCompletionSource _firstWriteStarted = new(
        TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource _releaseFirstWrite = new(
        TaskCreationOptions.RunContinuationsAsynchronously);
    private int _writeCallCount;

    public Task FirstWriteStarted => _firstWriteStarted.Task;

    public void ReleaseFirstWrite() => _releaseFirstWrite.TrySetResult();

    public byte[] GetBytes()
    {
        lock (_gate)
            return _bytes.ToArray();
    }

    public override bool CanRead => false;
    public override bool CanSeek => false;
    public override bool CanWrite => true;
    public override long Length => throw new NotSupportedException();
    public override long Position
    {
        get => throw new NotSupportedException();
        set => throw new NotSupportedException();
    }

    public override async ValueTask WriteAsync(
        ReadOnlyMemory<byte> buffer,
        CancellationToken cancellationToken = default)
    {
        if (Interlocked.Increment(ref _writeCallCount) == 1)
        {
            _firstWriteStarted.TrySetResult();
            await _releaseFirstWrite.Task.WaitAsync(cancellationToken);
        }

        cancellationToken.ThrowIfCancellationRequested();
        lock (_gate)
            _bytes.Write(buffer.Span);
    }

    public override Task FlushAsync(CancellationToken cancellationToken) =>
        Task.CompletedTask;

    public override void Flush() => throw new NotSupportedException();
    public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
}
