using PasswordManagerLocal.Common.Backend.Configuration;
using PasswordManagerLocal.Common.Backend.Constants;
using PasswordManagerLocal.Common.Backend.Hosting;
using PasswordManagerLocal.Windows.Backend.Security;
using System.Runtime.Versioning;

namespace PasswordManagerLocal.Windows.Backend;

[SupportedOSPlatform("windows")]
public static class WindowsBackendRuntimeFactory
{
    public static BackendRuntimeComposition Create()
    {
        var localApplicationData = Environment.GetFolderPath(
            Environment.SpecialFolder.LocalApplicationData,
            Environment.SpecialFolderOption.Create);

        if (string.IsNullOrWhiteSpace(localApplicationData))
            throw new InvalidOperationException("The Windows local application-data directory is unavailable.");

        var storagePaths = new BackendStoragePaths(
            Path.Combine(localApplicationData, ApplicationFileNames.AppFolderName));
        var runtime = BackendRuntimeFactory.Create(
            storagePaths,
            static () => new DpapiKeyProtector(),
            static () => new WindowsLocalDiscoveryNetworkLease());

        return new BackendRuntimeComposition(
            runtime,
            new BackendRuntimeLifetimeCoordinator(runtime),
            storagePaths.RootDirectory);
    }
}
