using Microsoft.Extensions.DependencyInjection;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using PasswordManagerLocal.Common.Backend.Abstractions.Services;
using PasswordManagerLocal.Common.Backend.Hosting;
using PasswordManagerLocal.Common.Tests.Fakes;

namespace PasswordManagerLocal.Common.Tests.Backend.Hosting;

[TestClass]
public sealed class BackendServiceHostTests
{
    [TestMethod]
    [TestCategory("Backend")]
    [TestCategory("Unit")]
    public async Task StartAndStop_StartsInRegistrationOrderAndStopsInReverseOrder()
    {
        var calls = new List<string>();
        var services = new ServiceCollection();
        services.AddSingleton<IBackendHostedService>(new FakeBackendHostedService("first", calls));
        services.AddSingleton<IBackendHostedService>(new FakeBackendHostedService("second", calls));

        await using var host = new BackendServiceHost(services.BuildServiceProvider());

        await host.StartAsync();
        await host.StopAsync();

        CollectionAssert.AreEqual(
            new[] { "start:first", "start:second", "stop:second", "stop:first" },
            calls);
    }

    [TestMethod]
    [TestCategory("Backend")]
    [TestCategory("Unit")]
    public async Task Start_WhenServiceFails_StopsServicesThatAlreadyStarted()
    {
        var calls = new List<string>();
        var services = new ServiceCollection();
        services.AddSingleton<IBackendHostedService>(new FakeBackendHostedService("started", calls));
        services.AddSingleton<IBackendHostedService>(new FakeBackendHostedService("failing", calls, throwOnStart: true));

        await using var host = new BackendServiceHost(services.BuildServiceProvider());

        await ExpectThrowsAsync<InvalidOperationException>(() => host.StartAsync());

        CollectionAssert.AreEqual(
            new[] { "start:started", "start:failing", "stop:failing", "stop:started" },
            calls);
    }


    [TestMethod]
    [TestCategory("Backend")]
    [TestCategory("Unit")]
    public async Task StopContinuesAcrossServicesAndAggregatesFailures()
    {
        var calls = new List<string>();
        var services = new ServiceCollection();
        services.AddSingleton<IBackendHostedService>(
            new FakeBackendHostedService("first", calls, throwOnStop: true));
        services.AddSingleton<IBackendHostedService>(
            new FakeBackendHostedService("second", calls, throwOnStop: true));
        var host = new BackendServiceHost(services.BuildServiceProvider());
        await host.StartAsync();

        var failure = await Assert.ThrowsAsync<AggregateException>(
            async () => await host.StopAsync());

        Assert.AreEqual(2, failure.Flatten().InnerExceptions.Count);
        CollectionAssert.AreEqual(
            new[] { "start:first", "start:second", "stop:second", "stop:first" },
            calls);
        await Assert.ThrowsAsync<AggregateException>(
            async () => await host.DisposeAsync());
    }

    private static async Task ExpectThrowsAsync<TException>(Func<Task> action) where TException : Exception
    {
        try
        {
            await action();
            Assert.Fail($"Expected {typeof(TException).Name}.");
        }
        catch (TException)
        {
        }
    }
}
