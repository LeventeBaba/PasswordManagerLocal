using Microsoft.VisualStudio.TestTools.UnitTesting;
using PasswordManagerLocal.Windows.EndpointRpc.Authorization;
using PasswordManagerLocal.Windows.Tests.EndpointRpc.Infrastructure;
using PasswordManagerLocal.Windows.Ipc.Contracts;
using PasswordManagerLocal.Windows.Ipc.Lifecycle;
using PasswordManagerLocal.Windows.Ipc.Protocol;
using PasswordManagerLocal.Windows.Ipc.Serialization;
using PasswordManagerLocal.Windows.Ipc.Server;

namespace PasswordManagerLocal.Windows.Tests.EndpointRpc.Authorization;

[TestClass]
public sealed class EndpointRpcAuthorizationTests
{
    [TestMethod]
    public void UiRoleCapabilityAndCurrentControlRegistrationAreRequired()
    {
        var expectedInstance = Guid.NewGuid();
        var resolver = new FakeEndpointUiRegistrationResolver
        {
            ExpectedProcessId = 1234,
            ExpectedWindowsSessionId = 7,
            ExpectedInstanceId = expectedInstance
        };
        var authorizer = new EndpointRpcConnectionAuthorizer(
            resolver,
            new ControllableEndpointRpcAdmissionPolicy());

        var wrongRole = authorizer.Authorize(CreateConnection(
            IpcPeerRole.TestClient, IpcCapabilities.EndpointRpc, 1234, 7, expectedInstance));
        var missingCapability = authorizer.Authorize(CreateConnection(
            IpcPeerRole.Ui, IpcCapabilities.Control, 1234, 7, expectedInstance));
        var wrongProcess = authorizer.Authorize(CreateConnection(
            IpcPeerRole.Ui, IpcCapabilities.EndpointRpc, 9999, 7, expectedInstance));
        var wrongSession = authorizer.Authorize(CreateConnection(
            IpcPeerRole.Ui, IpcCapabilities.EndpointRpc, 1234, 8, expectedInstance));
        var wrongInstance = authorizer.Authorize(CreateConnection(
            IpcPeerRole.Ui, IpcCapabilities.EndpointRpc, 1234, 7, Guid.NewGuid()));

        Assert.AreEqual(IpcErrorCode.UnexpectedPeerRole, wrongRole.ErrorCode);
        Assert.AreEqual(IpcErrorCode.UnsupportedCapability, missingCapability.ErrorCode);
        Assert.AreEqual(IpcErrorCode.UiNotRegistered, wrongProcess.ErrorCode);
        Assert.AreEqual(IpcErrorCode.UiNotRegistered, wrongSession.ErrorCode);
        Assert.AreEqual(IpcErrorCode.UiNotRegistered, wrongInstance.ErrorCode);
    }

    [TestMethod]
    public async Task DuplicateConnectionIsRejectedAndStaleDisconnectCannotReleaseNewerConnection()
    {
        var authorizer = new EndpointRpcConnectionAuthorizer(
            new FakeEndpointUiRegistrationResolver(),
            new ControllableEndpointRpcAdmissionPolicy());
        var first = CreateConnection(IpcPeerRole.Ui, IpcCapabilities.EndpointRpc);
        var second = CreateConnection(IpcPeerRole.Ui, IpcCapabilities.EndpointRpc);

        Assert.IsTrue(authorizer.Authorize(first).IsAuthorized);
        Assert.AreEqual(IpcErrorCode.UiAlreadyRegistered, authorizer.Authorize(second).ErrorCode);
        await ReleaseAsync(authorizer, first);
        Assert.IsTrue(authorizer.Authorize(second).IsAuthorized);
        await ReleaseAsync(authorizer, first);

        Assert.IsTrue(authorizer.IsAuthorizedConnection(second));
    }

    [TestMethod]
    public void NewControlRegistrationGenerationInvalidatesOldEndpointImmediately()
    {
        var resolver = new FakeEndpointUiRegistrationResolver { RegistrationGeneration = 4 };
        var authorizer = new EndpointRpcConnectionAuthorizer(
            resolver,
            new ControllableEndpointRpcAdmissionPolicy());
        var connection = CreateConnection(IpcPeerRole.Ui, IpcCapabilities.EndpointRpc);
        Assert.IsTrue(authorizer.Authorize(connection).IsAuthorized);
        Assert.IsTrue(authorizer.IsAuthorizedConnection(connection));

        resolver.RegistrationGeneration = 5;

        Assert.IsFalse(authorizer.IsAuthorizedConnection(connection));
    }

    [TestMethod]
    public async Task NewRegistrationMayReplaceStaleEndpointAndOldDisconnectCannotReleaseIt()
    {
        var instanceId = Guid.NewGuid();
        var resolver = new FakeEndpointUiRegistrationResolver
        {
            ExpectedProcessId = 1234,
            ExpectedWindowsSessionId = 7,
            ExpectedInstanceId = instanceId,
            RegistrationGeneration = 4
        };
        var authorizer = new EndpointRpcConnectionAuthorizer(
            resolver,
            new ControllableEndpointRpcAdmissionPolicy());
        var first = CreateConnection(
            IpcPeerRole.Ui, IpcCapabilities.EndpointRpc, 1234, 7, instanceId);
        Assert.IsTrue(authorizer.Authorize(first).IsAuthorized);

        resolver.RegistrationGeneration = 5;
        var replacement = CreateConnection(
            IpcPeerRole.Ui, IpcCapabilities.EndpointRpc, 1234, 7, instanceId);

        Assert.IsTrue(authorizer.Authorize(replacement).IsAuthorized);
        await ReleaseAsync(authorizer, first);
        Assert.IsTrue(authorizer.IsAuthorizedConnection(replacement));
    }

    [TestMethod]
    public void EndpointOperationsRequireActiveCurrentConnection()
    {
        var resolver = new FakeEndpointUiRegistrationResolver();
        var admissionPolicy = new ControllableEndpointRpcAdmissionPolicy();
        var connectionAuthorizer = new EndpointRpcConnectionAuthorizer(
            resolver,
            admissionPolicy);
        var authorizer = new EndpointRpcOperationAuthorizer(
            connectionAuthorizer,
            admissionPolicy);
        var connection = CreateConnection(IpcPeerRole.Ui, IpcCapabilities.EndpointRpc);
        var endpointRequest = CreateRequestContext(connection, IpcOperationId.EndpointRpcRequest);
        var readinessRequest = CreateRequestContext(connection, IpcOperationId.EndpointSessionReady);

        Assert.IsFalse(authorizer.Authorize(endpointRequest).IsAuthorized);
        Assert.IsTrue(connectionAuthorizer.Authorize(connection).IsAuthorized);
        Assert.IsTrue(authorizer.Authorize(endpointRequest).IsAuthorized);
        Assert.IsTrue(authorizer.Authorize(readinessRequest).IsAuthorized);
        Assert.IsFalse(authorizer.Authorize(
            CreateRequestContext(connection, IpcOperationId.GetAgentStatus)).IsAuthorized);

        resolver.IsRegisteredResult = false;
        Assert.IsFalse(authorizer.Authorize(endpointRequest).IsAuthorized);
    }

    [TestMethod]
    public void ClosedAdmissionRejectsHandshakeAndExistingConnectionOperations()
    {
        var admissionPolicy = new ControllableEndpointRpcAdmissionPolicy();
        var resolver = new FakeEndpointUiRegistrationResolver();
        var connectionAuthorizer = new EndpointRpcConnectionAuthorizer(
            resolver,
            admissionPolicy);
        var operationAuthorizer = new EndpointRpcOperationAuthorizer(
            connectionAuthorizer,
            admissionPolicy);
        var connection = CreateConnection(IpcPeerRole.Ui, IpcCapabilities.EndpointRpc);

        Assert.IsTrue(connectionAuthorizer.Authorize(connection).IsAuthorized);
        admissionPolicy.CanAcceptConnection = false;

        var replacement = connectionAuthorizer.Authorize(
            CreateConnection(IpcPeerRole.Ui, IpcCapabilities.EndpointRpc));
        var request = operationAuthorizer.Authorize(
            CreateRequestContext(connection, IpcOperationId.EndpointRpcRequest));

        Assert.IsFalse(replacement.IsAuthorized);
        Assert.AreEqual(IpcErrorCode.AgentUnavailable, replacement.ErrorCode);
        Assert.IsFalse(request.IsAuthorized);
        Assert.AreEqual(IpcErrorCode.AgentUnavailable, request.ErrorCode);
        Assert.IsFalse(connectionAuthorizer.IsAuthorizedConnection(connection));
    }

    private static Task ReleaseAsync(
        EndpointRpcConnectionAuthorizer authorizer,
        IpcConnectionContext connection) =>
        authorizer.OnConnectionLifecycleChangedAsync(
            new IpcConnectionLifecycleNotification(
                connection.ConnectionId,
                IpcConnectionLifecycleState.Disconnected,
                connection,
                IpcDisconnectKind.Clean,
                DateTimeOffset.UtcNow)).AsTask();

    private static IpcConnectionContext CreateConnection(
        IpcPeerRole role,
        IpcCapabilities capabilities,
        int processId = 1234,
        int windowsSessionId = 7,
        Guid? instanceId = null) =>
        new(
            Guid.NewGuid(),
            role,
            processId,
            windowsSessionId,
            instanceId ?? Guid.NewGuid(),
            capabilities);

    private static IpcRequestContext CreateRequestContext(
        IpcConnectionContext connection,
        IpcOperationId operationId) =>
        new(
            connection,
            new IpcRequestEnvelope(2, operationId, null),
            new WindowsIpcSerializer());
}
