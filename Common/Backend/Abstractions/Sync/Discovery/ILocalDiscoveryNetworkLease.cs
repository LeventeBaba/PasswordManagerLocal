namespace PasswordManagerLocal.Common.Backend.Abstractions.Sync.Discovery;

public interface ILocalDiscoveryNetworkLease
{
    ValueTask AcquireAsync(CancellationToken cancellationToken = default);
    ValueTask ReleaseAsync();
}
