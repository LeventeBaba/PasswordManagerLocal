namespace PasswordManagerLocal.Windows.Tests.IPC.Infrastructure;

internal sealed class ConcurrencyDetectingWriteStream : Stream
{
    private readonly MemoryStream _bytes = new();
    private readonly object _gate = new();
    private int _activeWrites;
    private int _maximumConcurrentWrites;

    public int MaximumConcurrentWrites => Volatile.Read(ref _maximumConcurrentWrites);

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
        var active = Interlocked.Increment(ref _activeWrites);
        UpdateMaximum(active);
        try
        {
            await Task.Yield();
            cancellationToken.ThrowIfCancellationRequested();
            lock (_gate)
                _bytes.Write(buffer.Span);
        }
        finally
        {
            Interlocked.Decrement(ref _activeWrites);
        }
    }

    public override Task FlushAsync(CancellationToken cancellationToken) =>
        Task.CompletedTask;

    public override void Flush() => throw new NotSupportedException();
    public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

    private void UpdateMaximum(int active)
    {
        while (true)
        {
            var current = Volatile.Read(ref _maximumConcurrentWrites);
            if (current >= active ||
                Interlocked.CompareExchange(ref _maximumConcurrentWrites, active, current) == current)
            {
                return;
            }
        }
    }
}
