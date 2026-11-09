using Microsoft.VisualStudio.TestTools.UnitTesting;
using PasswordManagerLocal.Windows.Tests.EndpointRpc.Infrastructure;

namespace PasswordManagerLocal.Windows.Tests.EndpointRpc.Frontend;

[TestClass]
public sealed class BoundedUiTestRunnerTests
{
    [TestMethod]
    public async Task StalledScenarioAndShutdownReportBothFailuresAndProgress()
    {
        var releaseScenario = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseShutdown = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var progress = new UiTestProgress();
        progress.Record("Intentional change-hint stall.");
        try
        {
            var failure = await Assert.ThrowsExactlyAsync<AssertFailedException>(() =>
                BoundedUiTestRunner.RunAsync(
                    _ => releaseScenario.Task,
                    () => releaseShutdown.Task,
                    progress,
                    TimeSpan.FromMilliseconds(100),
                    TimeSpan.FromMilliseconds(100),
                    TimeSpan.FromMilliseconds(100)).WaitAsync(TimeSpan.FromSeconds(5)));

            StringAssert.Contains(failure.Message, "Intentional change-hint stall.");
            StringAssert.Contains(failure.Message, "Scenario deadline reached");
            StringAssert.Contains(failure.Message, "Headless shutdown failed");
        }
        finally
        {
            releaseScenario.TrySetResult();
            releaseShutdown.TrySetResult();
        }
    }

    [TestMethod]
    public async Task ScenarioCancellationAllowsAsyncCleanupBeforeHeadlessShutdown()
    {
        var neverDelivered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var asyncCleanupCompleted = false;
        var shutdownRan = false;
        var progress = new UiTestProgress();
        await Assert.ThrowsExactlyAsync<AssertFailedException>(() =>
            BoundedUiTestRunner.RunAsync(
                async token =>
                {
                    try { await neverDelivered.Task.WaitAsync(token); }
                    finally
                    {
                        await Task.Yield();
                        asyncCleanupCompleted = true;
                    }
                },
                () =>
                {
                    Assert.IsTrue(asyncCleanupCompleted,
                        "The UI loop must remain available while canceled work cleans up.");
                    shutdownRan = true;
                    return Task.CompletedTask;
                },
                progress,
                TimeSpan.FromMilliseconds(100),
                TimeSpan.FromSeconds(2),
                TimeSpan.FromSeconds(1)).WaitAsync(TimeSpan.FromSeconds(5)));

        Assert.IsTrue(asyncCleanupCompleted);
        Assert.IsTrue(shutdownRan);
        StringAssert.Contains(progress.Describe(), "Headless shutdown completed.");
    }
}
