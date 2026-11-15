using System.Threading.Channels;
using PasswordManagerLocal.Common.Backend.Abstractions.Services;

namespace PasswordManagerLocal.Common.Backend.Services;

public sealed class RevocationDeliveryActivity : IRevocationDeliveryActivity
{
    private readonly Channel<bool> _refreshes = Channel.CreateBounded<bool>(new BoundedChannelOptions(1)
    { FullMode = BoundedChannelFullMode.DropWrite, SingleReader = true, SingleWriter = false });
    private int _active;
    public bool HasActiveWork => Volatile.Read(ref _active) != 0;
    public IDisposable Enter() { Interlocked.Increment(ref _active); return new Lease(this); }
    public void RequestRefresh() => _refreshes.Writer.TryWrite(true);
    public IAsyncEnumerable<bool> ReadRefreshesAsync(CancellationToken ct) => _refreshes.Reader.ReadAllAsync(ct);
    private sealed class Lease(RevocationDeliveryActivity owner) : IDisposable
    {
        private RevocationDeliveryActivity? _owner = owner;
        public void Dispose()
        {
            var value = Interlocked.Exchange(ref _owner, null);
            if (value is null) return;
            Interlocked.Decrement(ref value._active);
            value.RequestRefresh();
        }
    }
}
