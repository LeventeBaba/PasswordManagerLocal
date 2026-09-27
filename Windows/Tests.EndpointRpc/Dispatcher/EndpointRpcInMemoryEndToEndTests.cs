using Microsoft.VisualStudio.TestTools.UnitTesting;
using PasswordManagerLocal.Common.Contracts.Requests;
using PasswordManagerLocal.Windows.EndpointRpc.Client;
using PasswordManagerLocal.Windows.EndpointRpc.Serialization;
using PasswordManagerLocal.Windows.EndpointRpc.Server;
using PasswordManagerLocal.Windows.Tests.EndpointRpc.Infrastructure;
using PasswordManagerLocal.Windows.EndpointRpc.Validation;

namespace PasswordManagerLocal.Windows.Tests.EndpointRpc.Dispatcher;

[TestClass]
public sealed class EndpointRpcInMemoryEndToEndTests
{
    [TestMethod]
    public async Task RepresentativeEndpointFamiliesRoundTripThroughProxyIpcHandlerAndDispatcher()
    {
        var endpoints = new SuccessfulRecordingEndpoints();
        var serializer = new EndpointRpcSerializer();
        var validator = new EndpointRpcContractValidator();
        var codec = new EndpointRpcMessageCodec(serializer);
        await using var dispatcher = new EndpointRpcDispatcher(
            new FixedEndpointRpcEndpointAdapter(endpoints),
            serializer,
            validator,
            new EndpointRpcBackendErrorMapper());
        var handler = new EndpointRpcWindowsIpcRequestHandler(
            dispatcher,
            codec,
            validator,
            new ControllableEndpointRpcAdmissionPolicy());
        await using var transport = new InMemoryEndpointRpcTransport(handler, codec);
        var proxy = new NamedPipeEndpointsProxy(transport, serializer, validator);

        var loginToken = await proxy.LoginAsync(new LoginRequest
        {
            Username = "ValidUser1",
            Password = EndpointRpcTestData.Password(),
            RememberMe = true
        });
        var profile = await proxy.GetUserProfileInfoAsync(loginToken);
        var devices = await proxy.GetUserDevicesAsync(loginToken);
        var saved = await proxy.GetSavedPasswordsAsync(loginToken);
        await proxy.AddPasswordTagAsync(loginToken, new NewPasswordTagRequest { Name = "Work", Color = "#FF14B8A6" });
        var restored = await proxy.RestoreRememberedSessionsAsync();
        await proxy.DeleteUserAccountAsync(loginToken, EndpointRpcTestData.Password());

        Assert.AreEqual(EndpointRpcTestData.Token, loginToken);
        Assert.AreEqual("ValidUser1", profile.Username);
        Assert.IsEmpty(devices);
        Assert.IsEmpty(saved.Passwords);
        CollectionAssert.Contains(endpoints.Calls, nameof(endpoints.LoginAsync));
        CollectionAssert.Contains(endpoints.Calls, nameof(endpoints.GetUserProfileInfoAsync));
        CollectionAssert.Contains(endpoints.Calls, nameof(endpoints.GetUserDevicesAsync));
        CollectionAssert.Contains(endpoints.Calls, nameof(endpoints.GetSavedPasswordsAsync));
        CollectionAssert.Contains(endpoints.Calls, nameof(endpoints.AddPasswordTagAsync));
        CollectionAssert.AreEqual(new[] { EndpointRpcTestData.Token }, restored.ToArray());
        CollectionAssert.Contains(endpoints.Calls, nameof(endpoints.DeleteUserAccountAsync));
    }
}
