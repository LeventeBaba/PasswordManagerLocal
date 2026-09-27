using PasswordManagerLocal.Common.Backend.Abstractions.Sync.Discovery;

namespace PasswordManagerLocal.Windows.Backend;

public sealed class WindowsLocalDiscoveryNetworkLease : ILocalDiscoveryNetworkLease
{
    public ValueTask AcquireAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return ValueTask.CompletedTask;
    }

    public ValueTask ReleaseAsync() => ValueTask.CompletedTask;
}
