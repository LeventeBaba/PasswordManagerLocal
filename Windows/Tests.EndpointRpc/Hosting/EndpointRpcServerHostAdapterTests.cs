using Microsoft.VisualStudio.TestTools.UnitTesting;
using PasswordManagerLocal.Windows.EndpointRpc.Authorization;
using PasswordManagerLocal.Windows.EndpointRpc.Server;
using PasswordManagerLocal.Windows.Tests.EndpointRpc.Infrastructure;
using PasswordManagerLocal.Windows.Ipc.Server;
using PasswordManagerLocal.Windows.Ipc.Transport;

namespace PasswordManagerLocal.Windows.Tests.EndpointRpc.Hosting;

[TestClass]
public sealed class EndpointRpcServerHostAdapterTests
{
    [TestMethod]
    public async Task EndpointSessionFactoryIsDirectlyConsumableByGenericHostContracts()
    {
        var connection = new HostAdapterTestConnection();
        await using var endpointFactory = CreateFactory();
        IWindowsIpcServerSessionFactory genericFactory = endpointFactory;

        var session = genericFactory.Create(connection);

        Assert.IsInstanceOfType(session, typeof(IEndpointRpcServerSession));
        await session.DisposeAsync();
        Assert.AreEqual(1, connection.DisposeCount);
    }

    [TestMethod]
    [Timeout(10_000)]
    public async Task CompletedEndpointSessionIsRemovedAndDisposedByGenericHost()
    {
        await using var listener = new QueuedEndpointConnectionListener();
        var connection = new HostAdapterTestConnection();
        listener.Enqueue(connection);
        await using var factory = CreateFactory();
        await using var host = new WindowsIpcServerHost(
            listener,
            factory,
            new WindowsIpcServerHostOptions(maximumActiveConnections: 1));

        await host.StartAsync();
        await connection.ReadStarted;
        await WaitUntilAsync(() => connection.DisposeCount == 1 && host.ActiveSessionCount == 0);

        Assert.AreEqual(1, connection.DisposeCount);
        Assert.AreEqual(0, host.ActiveSessionCount);
    }

    [TestMethod]
    [Timeout(10_000)]
    public async Task ExistingHostConnectionLimitRejectsExcessEndpointConnection()
    {
        await using var listener = new QueuedEndpointConnectionListener();
        var admitted = new HostAdapterTestConnection(blockReads: true);
        var rejected = new HostAdapterTestConnection();
        listener.Enqueue(admitted);
        listener.Enqueue(rejected);
        await using var factory = CreateFactory();
        await using var host = new WindowsIpcServerHost(
            listener,
            factory,
            new WindowsIpcServerHostOptions(maximumActiveConnections: 1));

        await host.StartAsync();
        await admitted.ReadStarted;
        await WaitUntilAsync(() => rejected.DisposeCount == 1);

        Assert.AreEqual(1, host.ActiveSessionCount);
        Assert.AreEqual(0, admitted.DisposeCount);
        Assert.AreEqual(1, rejected.DisposeCount);

        await host.StopAsync();
        Assert.AreEqual(1, admitted.DisposeCount);
        Assert.AreEqual(0, host.ActiveSessionCount);
    }

    [TestMethod]
    [Timeout(10_000)]
    public async Task HostShutdownDisposesAdmittedEndpointSession()
    {
        await using var listener = new QueuedEndpointConnectionListener();
        var connection = new HostAdapterTestConnection(blockReads: true);
        listener.Enqueue(connection);
        await using var factory = CreateFactory();
        await using var host = new WindowsIpcServerHost(
            listener,
            factory,
            new WindowsIpcServerHostOptions(maximumActiveConnections: 1));

        await host.StartAsync();
        await connection.ReadStarted;
        await WaitUntilAsync(() => host.ActiveSessionCount == 1);
        await host.StopAsync();

        Assert.AreEqual(1, connection.DisposeCount);
        Assert.AreEqual(0, host.ActiveSessionCount);
    }

    [TestMethod]
    public void EndpointAssemblyContainsNoAlternativeConnectionListener()
    {
        Assert.IsFalse(typeof(IWindowsIpcConnectionListener).IsAssignableFrom(
            typeof(EndpointRpcServerSessionFactory)));
        Assert.IsFalse(typeof(EndpointRpcServerSessionFactory).Assembly
            .GetTypes()
            .Any(type => typeof(IWindowsIpcConnectionListener).IsAssignableFrom(type)));
    }

    private static EndpointRpcServerSessionFactory CreateFactory()
    {
        var admissionPolicy = new ControllableEndpointRpcAdmissionPolicy();
        var authorizer = new EndpointRpcConnectionAuthorizer(
            new FakeEndpointUiRegistrationResolver(),
            admissionPolicy);
        return new EndpointRpcServerSessionFactory(
            new FixedEndpointRpcEndpointAdapter(new SuccessfulRecordingEndpoints()),
            authorizer,
            admissionPolicy);
    }

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        while (!condition())
            await Task.Delay(10, timeout.Token);
    }
}
