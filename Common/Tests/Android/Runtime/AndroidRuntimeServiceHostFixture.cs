using PasswordManagerLocal.Android.Runtime;
using PasswordManagerLocal.Common.Contracts.Endpoints;
using PasswordManagerLocal.Common.Backend.Hosting;
using PasswordManagerLocal.Common.Tests.Fakes;
using PasswordManagerLocal.Common.Tests.TestInfrastructure;

namespace PasswordManagerLocal.Common.Tests.Android.Runtime;

public sealed class AndroidRuntimeServiceHostFixture : IDisposable
{
    public AndroidRuntimeServiceHostFixture(
        BackendTestHost backendTestHost,
        AndroidRuntimeServiceHost host,
        FakeBackendRuntime runtime,
        BackendRuntimeLifetimeCoordinator coordinator,
        FakeAndroidRuntimeCompositionFactory factory,
        FakeBackgroundSyncSettingsStore settings,
        FakeAndroidForegroundServiceController platform,
        FakeAndroidSecureStorageAvailability secureStorage,
        IEndpoints endpoints)
    {
        BackendTestHost = backendTestHost;
        Host = host;
        Runtime = runtime;
        Coordinator = coordinator;
        Factory = factory;
        Settings = settings;
        Platform = platform;
        SecureStorage = secureStorage;
        Endpoints = endpoints;
    }

    public BackendTestHost BackendTestHost { get; }
    public AndroidRuntimeServiceHost Host { get; }
    public FakeBackendRuntime Runtime { get; }
    public BackendRuntimeLifetimeCoordinator Coordinator { get; }
    public FakeAndroidRuntimeCompositionFactory Factory { get; }
    public FakeBackgroundSyncSettingsStore Settings { get; }
    public FakeAndroidForegroundServiceController Platform { get; }
    public FakeAndroidSecureStorageAvailability SecureStorage { get; }
    public IEndpoints Endpoints { get; }

    public void Dispose() => BackendTestHost.Dispose();
}
