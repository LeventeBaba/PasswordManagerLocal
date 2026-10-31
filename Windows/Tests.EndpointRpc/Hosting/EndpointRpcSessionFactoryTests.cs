using Microsoft.VisualStudio.TestTools.UnitTesting;
using PasswordManagerLocal.Windows.EndpointRpc.Authorization;
using PasswordManagerLocal.Windows.EndpointRpc.Server;
using PasswordManagerLocal.Windows.Tests.EndpointRpc.Infrastructure;
using PasswordManagerLocal.Windows.Ipc.Server;

namespace PasswordManagerLocal.Windows.Tests.EndpointRpc.Hosting;

[TestClass]
public sealed class EndpointRpcSessionFactoryTests
{
    [TestMethod]
    public async Task CreateThroughSharedFactoryContractReturnsSessionThatOwnsConnection()
    {
        var connection = new HostAdapterTestConnection();
        await using var endpointFactory = CreateFactory();
        IWindowsIpcServerSessionFactory genericFactory = endpointFactory;

        var session = genericFactory.Create(connection);

        Assert.IsInstanceOfType(session, typeof(WindowsIpcServerConnectionSession));
        await session.DisposeAsync();
        Assert.AreEqual(1, connection.DisposeCount);
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

}
