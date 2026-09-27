using Microsoft.VisualStudio.TestTools.UnitTesting;
using PasswordManagerLocal.Windows.Agent.Hosting;
using PasswordManagerLocal.Windows.Ipc.Contracts;

namespace PasswordManagerLocal.Windows.Tests.IPC.Agent;

[TestClass]
public sealed class WindowsAgentAdmissionGateTests
{
    [TestMethod]
    public async Task ClosingRejectsNewAdmissionsWhileExistingAdmissionDrains()
    {
        var gate = new WindowsAgentAdmissionGate();
        gate.Open();
        Assert.IsTrue(gate.TryEnter(out var lease));

        gate.ClosePermanently();
        var drain = gate.WaitForDrainAsync();

        Assert.AreEqual(AgentAdmissionState.Closing, gate.State);
        Assert.IsFalse(gate.TryEnter(out _));
        Assert.IsFalse(drain.IsCompleted);

        lease!.Dispose();
        await drain;

        Assert.AreEqual(AgentAdmissionState.Closed, gate.State);
        Assert.IsFalse(gate.IsOpen);
    }

    [TestMethod]
    public async Task OpenGateCanDrainCurrentAdmissionsWithoutClosing()
    {
        var gate = new WindowsAgentAdmissionGate();
        gate.Open();
        Assert.IsTrue(gate.TryEnter(out var lease));

        var drain = gate.WaitForDrainAsync();
        Assert.IsFalse(drain.IsCompleted);

        lease!.Dispose();
        await drain;

        Assert.AreEqual(AgentAdmissionState.Open, gate.State);
        Assert.IsTrue(gate.IsOpen);
    }

    [TestMethod]
    public void PermanentlyClosedGateCannotReopenAfterCriticalFailure()
    {
        var gate = new WindowsAgentAdmissionGate();
        gate.Open();
        gate.ClosePermanently();

        Assert.ThrowsExactly<InvalidOperationException>(gate.Open);
        Assert.AreEqual(AgentAdmissionState.Closed, gate.State);
    }
}
