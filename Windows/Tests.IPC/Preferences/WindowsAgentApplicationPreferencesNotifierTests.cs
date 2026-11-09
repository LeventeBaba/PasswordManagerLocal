using Microsoft.VisualStudio.TestTools.UnitTesting;
using PasswordManagerLocal.Common.Contracts.Runtime;
using PasswordManagerLocal.Windows.Frontend.Settings;
using PasswordManagerLocal.Windows.Tests.IPC.Infrastructure;

namespace PasswordManagerLocal.Windows.Tests.IPC.Preferences;

[TestClass]
public sealed class WindowsAgentApplicationPreferencesNotifierTests
{
    [TestMethod]
    public async Task LanguageNotificationSendsPayloadFreeReloadThroughControlConnection()
    {
        var connection = new FakeWindowsAgentControlConnection();
        using var notifier = new WindowsAgentApplicationPreferencesNotifier(connection);

        await notifier.NotifyLanguagePersistedAsync();

        Assert.AreEqual(1, connection.ReloadApplicationPreferencesCount);
    }
    [TestMethod]
    public async Task RejectedReloadIsReportedAsNotificationFailure()
    {
        var connection = new FakeWindowsAgentControlConnection
        {
            ReloadApplicationPreferencesResult = false
        };
        using var notifier = new WindowsAgentApplicationPreferencesNotifier(connection);

        await Assert.ThrowsExactlyAsync<InvalidOperationException>(
            () => notifier.NotifyLanguagePersistedAsync());

        Assert.AreEqual(1, connection.ReloadApplicationPreferencesCount);
    }

    [TestMethod]
    public async Task StartupNotificationsCompleteLocallyAndCoalesceUntilBackendIsReady()
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var connection = new FakeWindowsAgentControlConnection { ReloadApplicationPreferencesEntered = entered };
        var runtime = new NotificationRuntime();
        using var notifier = new WindowsAgentApplicationPreferencesNotifier(connection, runtime);

        await notifier.NotifyLanguagePersistedAsync();
        await notifier.NotifyLanguagePersistedAsync();
        Assert.AreEqual(0, connection.ReloadApplicationPreferencesCount);
        Assert.AreEqual(0, connection.EnsureConnectedCount);

        runtime.MarkReady();
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(2));

        Assert.AreEqual(1, connection.ReloadApplicationPreferencesCount);
        Assert.AreEqual(0, connection.EnsureConnectedCount);
    }

    [TestMethod]
    public async Task DisposalDropsDeferredNotificationAndUnsubscribesFromReadiness()
    {
        var connection = new FakeWindowsAgentControlConnection();
        var runtime = new NotificationRuntime();
        var notifier = new WindowsAgentApplicationPreferencesNotifier(connection, runtime);
        await notifier.NotifyLanguagePersistedAsync();
        notifier.Dispose();

        runtime.MarkReady();

        Assert.AreEqual(0, connection.ReloadApplicationPreferencesCount);
        Assert.AreEqual(0, connection.EnsureConnectedCount);
    }

    private sealed class NotificationRuntime : IBackendRuntimeClient
    {
        public BackendRuntimeSnapshot Snapshot { get; private set; } =
            new(BackendRuntimeState.Starting, BackendRuntimeFailureKind.None, null, DateTimeOffset.UtcNow);
        public event EventHandler<BackendRuntimeStateChangedEventArgs>? StateChanged;
        public void MarkReady()
        {
            var previous = Snapshot;
            Snapshot = new BackendRuntimeSnapshot(BackendRuntimeState.Ready, BackendRuntimeFailureKind.None, null, DateTimeOffset.UtcNow);
            StateChanged?.Invoke(this, new BackendRuntimeStateChangedEventArgs(previous, Snapshot));
        }
        public Task ConnectAsync(CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task WaitUntilReadyAsync(CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task ResetDatabaseAndRestartAsync(CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

}
