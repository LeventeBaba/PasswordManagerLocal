using PasswordManagerLocal.Common.Backend.Abstractions.Security;
using PasswordManagerLocal.Common.Backend.Abstractions.Sync.Discovery;
using PasswordManagerLocal.Common.Backend.Configuration;

namespace PasswordManagerLocal.Common.Backend.Hosting;

public static class BackendRuntimeFactory
{
    public static IBackendRuntime Create(
        BackendStoragePaths storagePaths,
        Func<IKeyProtector> keyProtectorFactory,
        Func<ILocalDiscoveryNetworkLease> discoveryNetworkLeaseFactory) =>
        new BackendRuntime(
            new BackendRuntimeOptions(
                storagePaths,
                keyProtectorFactory,
                discoveryNetworkLeaseFactory));
}
