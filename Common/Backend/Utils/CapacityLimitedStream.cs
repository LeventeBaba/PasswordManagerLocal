using PasswordManagerLocal.Common.Contracts.Errors;

namespace PasswordManagerLocal.Common.Backend.Utils;

/// <summary>Limits bytes crossing a codec boundary, including decompressed data and trailing input.</summary>
internal sealed class CapacityLimitedStream(Stream inner, long capacity, bool leaveOpen = true) : Stream
{
    private long _transferred;
    private bool _exceeded;
    public override bool CanRead => inner.CanRead;
    public override bool CanSeek => false;
    public override bool CanWrite => inner.CanWrite;
    public override long Length => throw new NotSupportedException();
    public override long Position { get => _transferred; set => throw new NotSupportedException(); }
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Flush() => inner.Flush();
    public override Task FlushAsync(CancellationToken ct) => inner.FlushAsync(ct);

    private int ReadSize(int requested) => (int)Math.Min(requested, Math.Max(0, capacity - _transferred) + 1);
    private void Charge(int count)
    {
        if (_exceeded || count > capacity - _transferred)
        {
            _exceeded = true;
            throw new StorageCapacityExceededException();
        }
        _transferred += count;
    }
    public override int Read(byte[] buffer, int offset, int count) => Read(buffer.AsSpan(offset, count));
    public override int Read(Span<byte> buffer)
    {
        var count = inner.Read(buffer[..ReadSize(buffer.Length)]);
        Charge(count);
        return count;
    }
    public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken ct) =>
        ReadAsync(buffer.AsMemory(offset, count), ct).AsTask();
    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default)
    {
        var count = await inner.ReadAsync(buffer[..ReadSize(buffer.Length)], ct);
        Charge(count);
        return count;
    }
    public override void Write(byte[] buffer, int offset, int count) => Write(buffer.AsSpan(offset, count));
    public override void Write(ReadOnlySpan<byte> buffer)
    {
        Charge(buffer.Length);
        inner.Write(buffer);
    }
    public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken ct) =>
        WriteAsync(buffer.AsMemory(offset, count), ct).AsTask();
    public override async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        Charge(buffer.Length);
        await inner.WriteAsync(buffer, ct);
    }
    protected override void Dispose(bool disposing)
    {
        if (disposing && !leaveOpen) inner.Dispose();
        base.Dispose(disposing);
    }
    public override async ValueTask DisposeAsync()
    {
        if (!leaveOpen) await inner.DisposeAsync();
        GC.SuppressFinalize(this);
    }
}
