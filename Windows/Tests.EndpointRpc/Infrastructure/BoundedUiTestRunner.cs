using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace PasswordManagerLocal.Windows.Tests.EndpointRpc.Infrastructure;

/// <summary>
/// Keeps the test runner independent of a blocked UI thread, including during shutdown.
/// The scenario token cancels application waits, not the headless dispatcher's message loop.
/// </summary>
internal static class BoundedUiTestRunner
{
    public static async Task RunAsync(
        Func<CancellationToken, Task> scenario,
        Func<Task> shutdown,
        UiTestProgress progress,
        TimeSpan scenarioTimeout,
        TimeSpan completionGrace,
        TimeSpan shutdownTimeout)
    {
        using var cancellation = new CancellationTokenSource(scenarioTimeout);
        var scenarioToken = cancellation.Token;
        using var registration = scenarioToken.Register(
            () => progress.Record("Scenario deadline reached; canceling scenario waits."));
        // Invoking either delegate can block before it returns a Task. Keep both invocations
        // off the test runner thread so WaitAsync can still report that failure.
        var scenarioTask = Task.Run(() => scenario(scenarioToken));
        Exception? scenarioFailure = null;
        Exception? shutdownFailure = null;
        try
        {
            await scenarioTask.WaitAsync(scenarioTimeout + completionGrace).ConfigureAwait(false);
            if (cancellation.IsCancellationRequested)
                throw new TimeoutException("The scenario finished after its deadline.");
        }
        catch (Exception exception)
        {
            scenarioFailure = exception;
            progress.Record($"Scenario failed: {exception.GetType().Name}: {exception.Message}");
            ObserveFailure(scenarioTask);
        }

        progress.Record("Headless shutdown started.");
        var shutdownTask = Task.Run(shutdown);
        try
        {
            await shutdownTask.WaitAsync(shutdownTimeout).ConfigureAwait(false);
            progress.Record("Headless shutdown completed.");
        }
        catch (Exception exception)
        {
            shutdownFailure = exception;
            progress.Record($"Headless shutdown failed: {exception.GetType().Name}: {exception.Message}");
            ObserveFailure(shutdownTask);
        }

        if (scenarioFailure is not null || shutdownFailure is not null)
        {
            throw new AssertFailedException(
                $"Windows UI scenario failed. Scenario: {scenarioFailure?.ToString() ?? "completed"}" +
                $"{Environment.NewLine}Shutdown: {shutdownFailure?.ToString() ?? "completed"}" +
                $"{Environment.NewLine}Progress:{Environment.NewLine}{progress.Describe()}");
        }
    }

    public static void ObserveFailure(Task task) =>
        _ = task.ContinueWith(
            completed => { _ = completed.Exception; },
            CancellationToken.None,
            TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);
}
