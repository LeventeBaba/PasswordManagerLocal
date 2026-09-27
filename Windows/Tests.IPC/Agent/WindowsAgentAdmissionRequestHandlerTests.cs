using Microsoft.VisualStudio.TestTools.UnitTesting;
using PasswordManagerLocal.Windows.Agent.Hosting;
using PasswordManagerLocal.Windows.Ipc.Contracts;
using PasswordManagerLocal.Windows.Ipc.Lifecycle;
using PasswordManagerLocal.Windows.Ipc.Protocol;
using PasswordManagerLocal.Windows.Ipc.Serialization;
using PasswordManagerLocal.Windows.Ipc.Server;
using PasswordManagerLocal.Windows.Tests.IPC.Infrastructure;

namespace PasswordManagerLocal.Windows.Tests.IPC.Agent;

[TestClass]
public sealed class WindowsAgentAdmissionRequestHandlerTests
{
    [TestMethod]
    public async Task OpenAdmissionInvokesWrappedControlMutation()
    {
        var gate = new WindowsAgentAdmissionGate();
        gate.Open();
        var invocationCount = 0;
        var handler = new WindowsAgentAdmissionRequestHandler(
            gate,
            new DelegateWindowsIpcRequestHandler(
                IpcOperationId.RequestUiOpen,
                (context, _) =>
                {
                    invocationCount++;
                    return Task.FromResult(context.Success());
                }),
            AgentLocalizationTestFactory.CreateEnglish());

        var response = await handler.HandleAsync(CreateContext(), CancellationToken.None);

        Assert.IsTrue(response.IsSuccess);
        Assert.AreEqual(1, invocationCount);
    }

    [TestMethod]
    public async Task ClosedAdmissionRejectsWrappedControlMutationBeforeInvocation()
    {
        var gate = new WindowsAgentAdmissionGate();
        gate.Open();
        gate.ClosePermanently();
        var invocationCount = 0;
        var handler = new WindowsAgentAdmissionRequestHandler(
            gate,
            new DelegateWindowsIpcRequestHandler(
                IpcOperationId.RequestUiOpen,
                (context, _) =>
                {
                    invocationCount++;
                    return Task.FromResult(context.Success());
                }),
            AgentLocalizationTestFactory.CreateEnglish());

        var response = await handler.HandleAsync(CreateContext(), CancellationToken.None);

        Assert.IsFalse(response.IsSuccess);
        Assert.AreEqual(IpcErrorCode.AgentUnavailable, response.Error?.ErrorCode);
        Assert.AreEqual(0, invocationCount);
    }

    private static IpcRequestContext CreateContext() => new(
        new IpcConnectionContext(
            Guid.NewGuid(),
            IpcPeerRole.Ui,
            PeerProcessId: 200,
            PeerWindowsSessionId: 2,
            PeerSessionId: Guid.NewGuid(),
            PeerCapabilities: IpcCapabilities.Control),
        new IpcRequestEnvelope(
            CorrelationId: 17,
            OperationId: IpcOperationId.RequestUiOpen,
            Payload: null),
        new WindowsIpcSerializer());
}
