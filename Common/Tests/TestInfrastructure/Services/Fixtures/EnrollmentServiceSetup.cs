using Microsoft.Extensions.DependencyInjection;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using PasswordManagerLocal.Common.Contracts.Responses;
using PasswordManagerLocal.Common.Backend.Services;
using PasswordManagerLocal.Common.Tests.Fakes;

using MSTestAssert = Microsoft.VisualStudio.TestTools.UnitTesting.Assert;

namespace PasswordManagerLocal.Common.Tests.TestInfrastructure.Services.Fixtures;

internal sealed record EnrollmentServiceSetup(
    DeviceEnrollmentService Service,
    ServiceProvider Provider,
    FakeSyncRuntimeService Runtime,
    FakeBackendExecutionProfileProvider ExecutionProfileProvider,
    FakeLocalDiscoveryService LocalDiscovery,
    FakeSyncTransportClientService Transport) : IDisposable
{
    public void Dispose()
    {
        Service.Dispose();
        Provider.Dispose();
    }
}
