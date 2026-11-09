using PasswordManagerLocal.Common.Backend.Abstractions.Security;
using PasswordManagerLocal.Common.Backend.Abstractions.Sync.Discovery;
using PasswordManagerLocal.Common.Backend.Configuration;

namespace PasswordManagerLocal.Common.Backend.Hosting;

public sealed class BackendRuntimeOptions
{
    public BackendRuntimeOptions(
        BackendStoragePaths storagePaths,
        Func<IKeyProtector> keyProtectorFactory,
        Func<ILocalDiscoveryNetworkLease> discoveryNetworkLeaseFactory)
        : this(storagePaths, keyProtectorFactory, discoveryNetworkLeaseFactory, null)
    {
    }

    internal BackendRuntimeOptions(
        BackendStoragePaths storagePaths,
        Func<IKeyProtector> keyProtectorFactory,
        Func<ILocalDiscoveryNetworkLease> discoveryNetworkLeaseFactory,
        Func<IKeyProtector, ILocalDiscoveryNetworkLease, BackendServiceHost>? serviceHostFactory)
    {
        StoragePaths = storagePaths ?? throw new ArgumentNullException(nameof(storagePaths));
        KeyProtectorFactory = keyProtectorFactory ?? throw new ArgumentNullException(nameof(keyProtectorFactory));
        DiscoveryNetworkLeaseFactory = discoveryNetworkLeaseFactory ?? throw new ArgumentNullException(nameof(discoveryNetworkLeaseFactory));
        ServiceHostFactory = serviceHostFactory;
    }

    public BackendStoragePaths StoragePaths { get; }
    public Func<IKeyProtector> KeyProtectorFactory { get; }
    public Func<ILocalDiscoveryNetworkLease> DiscoveryNetworkLeaseFactory { get; }
    internal Func<IKeyProtector, ILocalDiscoveryNetworkLease, BackendServiceHost>? ServiceHostFactory { get; }
}
