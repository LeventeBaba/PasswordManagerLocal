using System.Buffers;
using System.Threading.Channels;

namespace PasswordManagerLocal.Common.Backend.Utils;

internal sealed class ChannelStream : Stream
{
    private readonly Channel<ChannelStreamPooledSegment> _channel;
    private readonly int _segmentSize;
    private ChannelStreamPooledSegment _current;
    private int _currentPosition;
    private bool _hasCurrent;
    private bool _completed;
    private bool _disposed;

    internal ChannelStream(int segmentSize = 81920, int capacitySegments = 8)
    {
        if (segmentSize <= 0)
            throw new ArgumentOutOfRangeException(nameof(segmentSize));
        if (capacitySegments <= 0)
            throw new ArgumentOutOfRangeException(nameof(capacitySegments));

        _segmentSize = segmentSize;
        _channel = Channel.CreateBounded<ChannelStreamPooledSegment>(new BoundedChannelOptions(capacitySegments)
        {
            FullMode = BoundedChannelFullMode.Wait,
            SingleReader = true,
            SingleWriter = true
        });
    }

    public override bool CanRead => !_disposed;
    public override bool CanSeek => false;
    public override bool CanWrite => !_disposed && !_completed;
    public override long Length => throw new NotSupportedException();
    public override long Position
    {
        get => throw new NotSupportedException();
        set => throw new NotSupportedException();
    }

    public override void Flush() { }

    public override Task FlushAsync(CancellationToken cancellationToken) =>
        Task.CompletedTask;

    public override int Read(byte[] buffer, int offset, int count) =>
        ReadCoreAsync(buffer.AsMemory(offset, count), CancellationToken.None).AsTask().GetAwaiter().GetResult();

    public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
        ReadCoreAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

    public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) =>
        ReadCoreAsync(buffer, cancellationToken);

    public override void Write(byte[] buffer, int offset, int count) =>
        WriteCoreAsync(buffer.AsMemory(offset, count), CancellationToken.None).AsTask().GetAwaiter().GetResult();

    public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
        WriteCoreAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

    public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default) =>
        WriteCoreAsync(buffer, cancellationToken);

    internal void CompleteWriting(Exception? error = null)
    {
        if (_completed)
            return;

        _completed = true;
        _channel.Writer.TryComplete(error);
    }

    protected override void Dispose(bool disposing)
    {
        if (_disposed)
            return;

        if (disposing)
        {
            _disposed = true;
            CompleteWriting();
            ReleaseCurrentSegment();
            DrainQueuedSegments();
        }

        base.Dispose(disposing);
    }

    public override ValueTask DisposeAsync()
    {
        Dispose(true);
        GC.SuppressFinalize(this);
        return ValueTask.CompletedTask;
    }

    public override long Seek(long offset, SeekOrigin origin) =>
        throw new NotSupportedException();

    public override void SetLength(long value) =>
        throw new NotSupportedException();

    private async ValueTask<int> ReadCoreAsync(Memory<byte> destination, CancellationToken cancellationToken)
    {
        ThrowIfDisposed();
        if (destination.Length == 0)
            return 0;

        while (true)
        {
            if (_hasCurrent)
            {
                var remaining = _current.Length - _currentPosition;
                if (remaining > 0)
                {
                    var count = Math.Min(destination.Length, remaining);
                    _current.Buffer.AsMemory(_currentPosition, count).CopyTo(destination);
                    _currentPosition += count;
                    if (_currentPosition >= _current.Length)
                        ReleaseCurrentSegment();
                    return count;
                }

                ReleaseCurrentSegment();
            }

            if (!await _channel.Reader.WaitToReadAsync(cancellationToken).ConfigureAwait(false))
                return 0;

            if (_channel.Reader.TryRead(out var segment))
            {
                _current = segment;
                _currentPosition = 0;
                _hasCurrent = true;
            }
        }
    }

    private async ValueTask WriteCoreAsync(ReadOnlyMemory<byte> source, CancellationToken cancellationToken)
    {
        ThrowIfDisposed();
        if (_completed)
            throw new IOException("Writing has already completed.");

        while (!source.IsEmpty)
        {
            var count = Math.Min(source.Length, _segmentSize);
            var buffer = ArrayPool<byte>.Shared.Rent(count);
            var ownershipTransferred = false;
            try
            {
                source[..count].CopyTo(buffer.AsMemory(0, count));
                await _channel.Writer.WriteAsync(new ChannelStreamPooledSegment(buffer, count), cancellationToken).ConfigureAwait(false);
                ownershipTransferred = true;
            }
            finally
            {
                if (!ownershipTransferred)
                    ReturnSensitiveBuffer(buffer);
            }

            source = source[count..];
        }
    }

    private void ReleaseCurrentSegment()
    {
        if (!_hasCurrent)
            return;

        ReturnSensitiveBuffer(_current.Buffer);
        _current = default;
        _currentPosition = 0;
        _hasCurrent = false;
    }

    private void DrainQueuedSegments()
    {
        while (_channel.Reader.TryRead(out var segment))
            ReturnSensitiveBuffer(segment.Buffer);
    }


    private void ThrowIfDisposed()
    {
        if (_disposed)
            throw new ObjectDisposedException(nameof(ChannelStream));
    }

    private static void ReturnSensitiveBuffer(byte[] buffer) =>
        ArrayPool<byte>.Shared.Return(buffer, clearArray: true);

}
