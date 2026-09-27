using Microsoft.VisualStudio.TestTools.UnitTesting;
using PasswordManagerLocal.Windows.Agent.Hosting;
using PasswordManagerLocal.Windows.Ipc.Contracts;

namespace PasswordManagerLocal.Windows.Tests.IPC.Agent;

[TestClass]
public sealed class WindowsAgentExitRequestSinkTests
{
    [TestMethod]
    public async Task UserRequestedExitMapsToPermanentTrayExitOnce()
    {
        var observed = await ObserveReasonAsync(AgentExitReason.UserRequested);

        Assert.AreEqual(WindowsAgentShutdownReason.UserRequestedExit, observed);
    }

    [TestMethod]
    public async Task RestartExitReasonsMapToReplacementShutdown()
    {
        Assert.AreEqual(
            WindowsAgentShutdownReason.RestartRequired,
            await ObserveReasonAsync(AgentExitReason.ApplicationUpdate));
        Assert.AreEqual(
            WindowsAgentShutdownReason.RestartRequired,
            await ObserveReasonAsync(AgentExitReason.ProcessRestartRequired));
    }

    [TestMethod]
    public async Task RepeatedRequestsScheduleOneShutdown()
    {
        var coordinator = new WindowsAgentShutdownCoordinator();
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var requestCount = 0;
        coordinator.ShutdownRequested += (_, _) =>
        {
            requestCount++;
            completion.TrySetResult();
        };
        var sink = new WindowsAgentExitRequestSink(coordinator, TimeSpan.Zero);

        Assert.IsTrue(await sink.RequestExitAsync(
            new AgentExitRequestDto(AgentExitReason.UserRequested), CancellationToken.None));
        Assert.IsTrue(await sink.RequestExitAsync(
            new AgentExitRequestDto(AgentExitReason.UserRequested), CancellationToken.None));
        await completion.Task.WaitAsync(TimeSpan.FromSeconds(2));
        await Task.Delay(25);

        Assert.AreEqual(1, requestCount);
    }

    private static async Task<WindowsAgentShutdownReason> ObserveReasonAsync(AgentExitReason reason)
    {
        var coordinator = new WindowsAgentShutdownCoordinator();
        var completion = new TaskCompletionSource<WindowsAgentShutdownReason>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        coordinator.ShutdownRequested += (_, args) => completion.TrySetResult(args.Reason);
        var sink = new WindowsAgentExitRequestSink(coordinator, TimeSpan.Zero);

        Assert.IsTrue(await sink.RequestExitAsync(
            new AgentExitRequestDto(reason), CancellationToken.None));
        return await completion.Task.WaitAsync(TimeSpan.FromSeconds(2));
    }
}
