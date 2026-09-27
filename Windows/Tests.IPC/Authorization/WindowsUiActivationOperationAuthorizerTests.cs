using Microsoft.VisualStudio.TestTools.UnitTesting;
using PasswordManagerLocal.Windows.Ipc.Authorization;
using PasswordManagerLocal.Windows.Ipc.Contracts;
using PasswordManagerLocal.Windows.Ipc.Lifecycle;
using PasswordManagerLocal.Windows.Ipc.Protocol;
using PasswordManagerLocal.Windows.Ipc.Serialization;
using PasswordManagerLocal.Windows.Ipc.Server;

namespace PasswordManagerLocal.Windows.Tests.IPC.Authorization;

[TestClass]
public sealed class WindowsUiActivationOperationAuthorizerTests
{
    [TestMethod]
    public void TrustedAgentMayRequestUiShutdown()
    {
        var authorizer = new WindowsUiActivationOperationAuthorizer(() => 200);

        var decision = authorizer.Authorize(CreateContext(
            IpcPeerRole.Agent,
            processId: 200,
            command: UiActivationCommand.IntentionalAgentShutdown));

        Assert.IsTrue(decision.IsAuthorized);
    }

    [TestMethod]
    public void UntrustedRoleOrProcessCannotRequestUiShutdown()
    {
        var authorizer = new WindowsUiActivationOperationAuthorizer(() => 200);

        Assert.IsFalse(authorizer.Authorize(CreateContext(
            IpcPeerRole.Ui,
            processId: 200,
            command: UiActivationCommand.IntentionalAgentShutdown)).IsAuthorized);
        Assert.IsFalse(authorizer.Authorize(CreateContext(
            IpcPeerRole.Agent,
            processId: 201,
            command: UiActivationCommand.IntentionalAgentShutdown)).IsAuthorized);
    }

    [TestMethod]
    public void OrdinaryActivationDoesNotGrantArbitraryOperations()
    {
        var authorizer = new WindowsUiActivationOperationAuthorizer(() => 200);
        Assert.IsTrue(authorizer.Authorize(CreateContext(
            IpcPeerRole.Ui,
            processId: 100,
            command: UiActivationCommand.Activate)).IsAuthorized);

        var denied = authorizer.Authorize(new IpcRequestContext(
            CreateConnection(IpcPeerRole.Agent, 200),
            new IpcRequestEnvelope(1, IpcOperationId.Ping, Payload: null),
            new WindowsIpcSerializer()));
        Assert.IsFalse(denied.IsAuthorized);
    }

    private static IpcRequestContext CreateContext(
        IpcPeerRole role,
        int processId,
        UiActivationCommand command)
    {
        var serializer = new WindowsIpcSerializer();
        var request = new UiActivationRequestDto(
            command == UiActivationCommand.IntentionalAgentShutdown
                ? UiActivationReason.AgentRequest
                : UiActivationReason.UserLaunch,
            BringToForeground: command == UiActivationCommand.Activate,
            Command: command);
        return new IpcRequestContext(
            CreateConnection(role, processId),
            new IpcRequestEnvelope(
                1,
                IpcOperationId.RequestUiActivation,
                serializer.Serialize(request, WindowsIpcJsonContext.Default.UiActivationRequestDto)),
            serializer);
    }

    private static IpcConnectionContext CreateConnection(IpcPeerRole role, int processId) => new(
        Guid.NewGuid(),
        role,
        processId,
        PeerWindowsSessionId: 2,
        PeerSessionId: Guid.NewGuid(),
        PeerCapabilities: IpcCapabilities.UiActivation);
}
