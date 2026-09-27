using Microsoft.Extensions.DependencyInjection;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using PasswordManagerLocal.Common.Backend.Abstractions.Repositories;
using PasswordManagerLocal.Common.Backend.Abstractions.Services;
using PasswordManagerLocal.Common.Backend.Models;
using PasswordManagerLocal.Common.Backend.Services;
using PasswordManagerLocal.Common.Backend.Sync;
using PasswordManagerLocal.Common.Tests.Fakes;

using MSTestAssert = Microsoft.VisualStudio.TestTools.UnitTesting.Assert;
using PasswordManagerLocal.Common.Backend.Sync.Discovery;
using PasswordManagerLocal.Common.Backend.State;

namespace PasswordManagerLocal.Common.Tests.Backend.Services;

[TestClass]
public sealed class SyncRuntimeServiceTests
{
    [TestMethod]
    [TestCategory("Backend")]
    [TestCategory("Unit")]
    public async Task RefreshSyncEnabled_WhenAUserIsEnabled_EnablesIdentityAndStartsInOrder()
    {
        var calls = new List<string>();
        var localUsers = new FakeLocalUserDeviceRepository();
        await localUsers.AddAsync(new LocalUserDevice
        {
            UserId = Guid.NewGuid(),
            LocalDeviceIdentityId = Guid.NewGuid(),
            IsSyncOn = true
        });
        var identity = new FakeDeviceIdentityService { IsSyncOn = false };
        var service = CreateService(localUsers, identity, calls, out _, out _, out _);

        await service.RefreshSyncEnabledAsync();

        MSTestAssert.IsTrue(identity.IsSyncOn);
        MSTestAssert.AreEqual(1, identity.SetSyncOnCalls);
        MSTestAssert.AreEqual(SyncRuntimeState.Running, service.Snapshot.State);
        CollectionAssert.AreEqual(new[] { "start:early", "start:late" }, calls);
    }

    [TestMethod]
    [TestCategory("Backend")]
    [TestCategory("Unit")]
    public async Task RefreshSyncEnabled_WhenNoUserIsEnabled_StopsInReverseAndClearsRuntimeCaches()
    {
        var calls = new List<string>();
        var localUsers = new FakeLocalUserDeviceRepository();
        var identity = new FakeDeviceIdentityService { IsSyncOn = true };
        var service = CreateService(localUsers, identity, calls, out var syncIdentities, out var endpoints, out var tasks);
        var device = new Device
        {
            Id = Guid.NewGuid(),
            PublicKey = [1],
            SignPublicKey = [2],
            TlsCertFingerprint = "AABB",
            IsTrusted = true
        };
        syncIdentities.TryAdd(device);
        endpoints.AddOrUpdate(new DiscoveredDeviceEndpoint { Host = "host", Port = 26688, TlsCertFingerprint = "AABB" });

        await service.RefreshSyncEnabledAsync();

        MSTestAssert.IsFalse(identity.IsSyncOn);
        MSTestAssert.AreEqual(1, identity.SetSyncOnCalls);
        MSTestAssert.AreEqual(1, tasks.StopAllCalls);
        MSTestAssert.IsTrue(syncIdentities.IsEmpty());
        MSTestAssert.IsFalse(endpoints.TryGetByFingerprint("AABB", out _));
        MSTestAssert.AreEqual(SyncRuntimeState.Disabled, service.Snapshot.State);
        CollectionAssert.AreEqual(new[] { "stop:late", "stop:early" }, calls);
    }

    [TestMethod]
    [TestCategory("Backend")]
    [TestCategory("Unit")]
    public async Task EnrollmentOnlyMode_StartsWhileSyncIsDisabled_ThenStops()
    {
        var calls = new List<string>();
        var identity = new FakeDeviceIdentityService { IsSyncOn = false };
        var service = CreateService(new FakeLocalUserDeviceRepository(), identity, calls, out _, out _, out var tasks);

        await service.BeginEnrollmentOnlyAsync();
        await service.EndEnrollmentOnlyAsync();

        CollectionAssert.AreEqual(
            new[] { "start:early", "start:late", "stop:late", "stop:early" },
            calls);
        MSTestAssert.AreEqual(1, tasks.StopAllCalls);
        MSTestAssert.IsFalse(identity.IsSyncOn);
        MSTestAssert.AreEqual(SyncRuntimeState.Disabled, service.Snapshot.State);
    }

    [TestMethod]
    [TestCategory("Backend")]
    [TestCategory("Unit")]
    public async Task Start_WhenNeitherSyncNorEnrollmentIsEnabled_DoesNotStartServices()
    {
        var calls = new List<string>();
        var identity = new FakeDeviceIdentityService { IsSyncOn = false };
        var service = CreateService(new FakeLocalUserDeviceRepository(), identity, calls, out _, out _, out _);

        await service.StartAsync();

        MSTestAssert.HasCount(0, calls);
    }


    [TestMethod]
    [TestCategory("Backend")]
    [TestCategory("Unit")]
    public async Task Start_WhenControlledServiceFails_StopsControlledServicesInReverseOrder()
    {
        var calls = new List<string>();
        var localUsers = new FakeLocalUserDeviceRepository();
        var services = new ServiceCollection();
        services.AddSingleton<ILocalUserDeviceRepository>(localUsers);
        using var provider = services.BuildServiceProvider();
        var identity = new FakeDeviceIdentityService { IsSyncOn = true };
        var syncIdentities = new FakeSyncDeviceIdentityService();
        var endpoints = new DiscoveredDeviceEndpointRegistry();
        var tasks = new FakeDeviceSyncTaskService();
        ISyncControlledHostedService[] controlledServices =
        [
            new FakeControlledHostedService("early", 10, calls),
            new FakeControlledHostedService("failing", 20, calls, throwOnStart: true)
        ];
        var service = new SyncRuntimeService(
            provider.GetRequiredService<IServiceScopeFactory>(),
            identity,
            new EnrollmentRuntimeState(),
            syncIdentities,
            endpoints,
            tasks,
            controlledServices);

        await ExpectThrowsAsync<InvalidOperationException>(() => service.StartAsync());

        CollectionAssert.AreEqual(
            new[] { "start:early", "start:failing", "stop:failing", "stop:early" },
            calls);
        MSTestAssert.AreEqual(1, tasks.StopAllCalls);
        MSTestAssert.AreEqual(SyncRuntimeState.Degraded, service.Snapshot.State);
    }

    private static SyncRuntimeService CreateService(
        FakeLocalUserDeviceRepository localUsers,
        FakeDeviceIdentityService identity,
        List<string> calls,
        out FakeSyncDeviceIdentityService syncIdentities,
        out DiscoveredDeviceEndpointRegistry endpoints,
        out FakeDeviceSyncTaskService tasks)
    {
        var services = new ServiceCollection();
        services.AddSingleton<ILocalUserDeviceRepository>(localUsers);
        var provider = services.BuildServiceProvider();
        syncIdentities = new FakeSyncDeviceIdentityService();
        endpoints = new DiscoveredDeviceEndpointRegistry();
        tasks = new FakeDeviceSyncTaskService();
        ISyncControlledHostedService[] hostedServices =
        [
            new FakeControlledHostedService("late", 30, calls),
            new FakeControlledHostedService("early", 10, calls)
        ];

        return new SyncRuntimeService(
            provider.GetRequiredService<IServiceScopeFactory>(),
            identity,
            new EnrollmentRuntimeState(),
            syncIdentities,
            endpoints,
            tasks,
            hostedServices);
    }
    private static async Task ExpectThrowsAsync<TException>(Func<Task> action) where TException : Exception
    {
        try
        {
            await action();
            MSTestAssert.Fail($"Expected {typeof(TException).Name}.");
        }
        catch (TException)
        {
        }
    }

}
