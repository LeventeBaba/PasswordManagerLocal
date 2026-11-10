using Microsoft.Extensions.DependencyInjection;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using PasswordManagerLocal.Android.Runtime;
using PasswordManagerLocal.Common.Contracts.Endpoints;
using PasswordManagerLocal.Common.Backend.Hosting;
using PasswordManagerLocal.Common.Frontend.Services;
using PasswordManagerLocal.Common.Tests.Fakes;
using PasswordManagerLocal.Common.Tests.TestInfrastructure;

namespace PasswordManagerLocal.Common.Tests.Frontend.Services;

[TestClass]
public sealed class DeferredEndpointsTests
{
    [TestMethod]
    public async Task EachOperation_UsesTheActiveInteractiveSession()
    {
        using var host = new BackendTestHost();
        var runtime = new FakeBackendRuntime(host.Services.GetRequiredService<IEndpoints>())
        {
            ChangeSource = new FakeBackendChangeSource()
        };
        var lifetimeCoordinator = new BackendRuntimeLifetimeCoordinator(runtime);
        var composition = new BackendRuntimeComposition(runtime, lifetimeCoordinator, "test-data");
        await using var serviceHost = new AndroidRuntimeServiceHost(
            new FakeAndroidRuntimeCompositionFactory(() => composition),
            new FakeBackgroundSyncSettingsStore(false),
            new FakeAndroidForegroundServiceController(),
            new FakeAndroidSecureStorageAvailability { IsAvailable = true });
        await using var backendClient = await serviceHost.AttachInteractiveClientAsync();
        var endpoints = new DeferredEndpoints(backendClient);
        var token = Guid.NewGuid();

        _ = await endpoints.GetAuthSessionStatusAsync(token);
        _ = await endpoints.GetAuthSessionStatusAsync(token);

        Assert.AreEqual(1, runtime.OpenInteractiveSessionCalls);
    }
}
