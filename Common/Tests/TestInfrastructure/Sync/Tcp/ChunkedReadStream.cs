using Microsoft.VisualStudio.TestTools.UnitTesting;
using PasswordManagerLocal.Common.Backend.Constants;
using PasswordManagerLocal.Common.Backend.Sync;
using PasswordManagerLocal.Common.Backend.Sync.Tcp;
using System.Buffers.Binary;

using MSTestAssert = Microsoft.VisualStudio.TestTools.UnitTesting.Assert;

namespace PasswordManagerLocal.Common.Tests.TestInfrastructure.Sync.Tcp;

internal sealed class ChunkedReadStream : Stream
{
    private readonly Stream _inner;
    private readonly int _maxReadSize;

    public ChunkedReadStream(Stream inner, int maxReadSize)
    {
        _inner = inner;
        _maxReadSize = maxReadSize;
    }

    public override bool CanRead => _inner.CanRead;
    public override bool CanSeek => _inner.CanSeek;
    public override bool CanWrite => false;
    public override long Length => _inner.Length;
    public override long Position
    {
        get => _inner.Position;
        set => _inner.Position = value;
    }

    public override void Flush() => _inner.Flush();

    public override int Read(byte[] buffer, int offset, int count) =>
        _inner.Read(buffer, offset, Math.Min(count, _maxReadSize));

    public override int Read(Span<byte> buffer) =>
        _inner.Read(buffer[..Math.Min(buffer.Length, _maxReadSize)]);

    public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) =>
        _inner.ReadAsync(buffer[..Math.Min(buffer.Length, _maxReadSize)], cancellationToken);

    public override long Seek(long offset, SeekOrigin origin) => _inner.Seek(offset, origin);
    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

    protected override void Dispose(bool disposing)
    {
        if (disposing)
            _inner.Dispose();
        base.Dispose(disposing);
    }

    public override async ValueTask DisposeAsync()
    {
        await _inner.DisposeAsync();
        GC.SuppressFinalize(this);
    }
}
