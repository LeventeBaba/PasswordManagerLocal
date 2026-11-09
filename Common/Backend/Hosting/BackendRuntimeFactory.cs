using PasswordManagerLocal.Common.Backend.Abstractions.Security;
using PasswordManagerLocal.Common.Backend.Abstractions.Sync.Discovery;
using PasswordManagerLocal.Common.Backend.Configuration;

namespace PasswordManagerLocal.Common.Backend.Hosting;

public static class BackendRuntimeFactory
{
    public static BackendRuntimeComposition Create(
        BackendStoragePaths storagePaths,
        Func<IKeyProtector> keyProtectorFactory,
        Func<ILocalDiscoveryNetworkLease> discoveryNetworkLeaseFactory)
    {
        var runtime = new BackendRuntime(new BackendRuntimeOptions(
            storagePaths,
            keyProtectorFactory,
            discoveryNetworkLeaseFactory));
        return new BackendRuntimeComposition(
            runtime,
            new BackendRuntimeLifetimeCoordinator(runtime),
            storagePaths.RootDirectory);
    }
}
