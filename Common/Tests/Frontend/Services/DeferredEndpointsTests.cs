using Microsoft.Extensions.DependencyInjection;
using Microsoft.VisualStudio.TestTools.UnitTesting;
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
        var runtime = new FakeBackendRuntime(host.Services.GetRequiredService<IEndpoints>());
        var lifetimeCoordinator = new BackendRuntimeLifetimeCoordinator(runtime);
        await using var backendClient = new InProcessFrontendBackendClient(runtime, lifetimeCoordinator);
        await backendClient.ConnectAsync();
        var endpoints = new DeferredEndpoints(backendClient);
        var token = Guid.NewGuid();

        _ = await endpoints.GetAuthSessionStatusAsync(token);
        _ = await endpoints.GetAuthSessionStatusAsync(token);

        Assert.AreEqual(1, runtime.OpenInteractiveSessionCalls);
    }
}
