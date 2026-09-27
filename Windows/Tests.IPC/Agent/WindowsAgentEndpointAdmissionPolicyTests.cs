using Microsoft.VisualStudio.TestTools.UnitTesting;
using PasswordManagerLocal.Windows.Agent.Backend;
using PasswordManagerLocal.Windows.Agent.Endpoint;
using PasswordManagerLocal.Windows.Agent.Hosting;
using PasswordManagerLocal.Common.Contracts.Runtime;
using PasswordManagerLocal.Common.Contracts.BackgroundSync;
using PasswordManagerLocal.Windows.Ipc.Contracts;
using PasswordManagerLocal.Windows.Tests.IPC.Infrastructure;

namespace PasswordManagerLocal.Windows.Tests.IPC.Agent;

[TestClass]
public sealed class WindowsAgentEndpointAdmissionPolicyTests
{
    [TestMethod]
    public void RunningHealthyOpenAgentAcceptsEndpointConnection()
    {
        var context = CreateContext();

        Assert.IsTrue(context.Policy.CanAcceptConnection);
        Assert.IsTrue(context.Policy.TryEnterRequest(out var lease));
        lease!.Dispose();
    }

    [TestMethod]
    public void StoppedRestartableRuntimeAcceptsEndpointConnection()
    {
        var context = CreateContext();
        context.Owner.Snapshot = FakeWindowsAgentBackendRuntimeOwner.CreateSnapshot(
            ownerState: WindowsAgentBackendOwnerState.Ready,
            runtimeState: BackendRuntimeState.Stopped);

        Assert.IsTrue(context.Policy.CanAcceptConnection);
        Assert.IsTrue(context.Policy.TryEnterRequest(out var lease));
        lease!.Dispose();
    }

    [TestMethod]
    public void FailedStoppingRestartRequiredAndClosedAdmissionRejectEndpointConnection()
    {
        var failed = CreateContext();
        failed.State.MarkFailed("fatal failure");
        Assert.IsFalse(failed.Policy.CanAcceptConnection);

        var stopping = CreateContext();
        stopping.State.MarkStopping();
        Assert.IsFalse(stopping.Policy.CanAcceptConnection);

        var restart = CreateContext();
        restart.Owner.RequireProcessRestart(new IOException("cleanup failed"));
        Assert.IsFalse(restart.Policy.CanAcceptConnection);

        var closed = CreateContext();
        closed.Gate.ClosePermanently();
        Assert.IsFalse(closed.Policy.CanAcceptConnection);
        Assert.IsFalse(closed.Policy.TryEnterRequest(out _));

        var runtimeFailed = CreateContext();
        runtimeFailed.Owner.Snapshot = FakeWindowsAgentBackendRuntimeOwner.CreateSnapshot(
            ownerState: WindowsAgentBackendOwnerState.Ready,
            runtimeState: BackendRuntimeState.Failed,
            runtimeFailureKind: BackendRuntimeFailureKind.DatabaseCompatibility);
        Assert.IsFalse(runtimeFailed.Policy.CanAcceptConnection);
    }

    private static (
        WindowsAgentStateStore State,
        WindowsAgentAdmissionGate Gate,
        FakeWindowsAgentBackendRuntimeOwner Owner,
        WindowsAgentEndpointAdmissionPolicy Policy) CreateContext()
    {
        var state = new WindowsAgentStateStore();
        state.MarkRunning(DateTimeOffset.UtcNow);
        var gate = new WindowsAgentAdmissionGate();
        gate.Open();
        var owner = new FakeWindowsAgentBackendRuntimeOwner();
        var policy = new WindowsAgentEndpointAdmissionPolicy(
            gate,
            state,
            owner,
            () => WindowsAgentEndpointHostState.Ready);
        return (state, gate, owner, policy);
    }
}
