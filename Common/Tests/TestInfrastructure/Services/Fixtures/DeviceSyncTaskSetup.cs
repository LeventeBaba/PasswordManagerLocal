using Microsoft.Extensions.DependencyInjection;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using PasswordManagerLocal.Common.Backend.Abstractions.Persistence;
using PasswordManagerLocal.Common.Backend.Abstractions.Repositories;
using PasswordManagerLocal.Common.Backend.Abstractions.Services;
using PasswordManagerLocal.Common.Backend.Models;
using PasswordManagerLocal.Common.Backend.Services;
using PasswordManagerLocal.Common.Backend.Sync;
using PasswordManagerLocal.Common.Tests.Fakes;

using MSTestAssert = Microsoft.VisualStudio.TestTools.UnitTesting.Assert;
using PasswordManagerLocal.Common.Backend.Sync.Discovery;

namespace PasswordManagerLocal.Common.Tests.TestInfrastructure.Services.Fixtures;

internal sealed record DeviceSyncTaskSetup(
    DeviceSyncTaskService Service,
    ServiceProvider Provider,
    FakeSyncQueueRepository Queue,
    FakeUnitOfWork UnitOfWork,
    FakeSyncTransportClientService Transport,
    DiscoveredDeviceEndpointRegistry EndpointRegistry,
    FakeDeviceIdentityService Identity,
    Device Device,
    DiscoveredDeviceEndpoint Endpoint) : IDisposable
{
    public void Dispose()
    {
        Service.Dispose();
        Provider.Dispose();
    }
}
