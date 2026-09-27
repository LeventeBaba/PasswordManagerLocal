using PasswordManagerLocal.Common.Backend.Hosting;
using PasswordManagerLocal.Common.Backend.Models;
using PasswordManagerLocal.Common.Contracts.Runtime;
using PasswordManagerLocal.Common.Contracts.BackgroundSync;

namespace PasswordManagerLocal.Windows.Tests.IPC.Infrastructure;

internal sealed class FakeAgentOwnedBackendRuntime : IBackendRuntime
{
    public BackendRuntimeSnapshot Snapshot { get; set; } = new(
        BackendRuntimeState.NotStarted,
        BackendRuntimeFailureKind.None,
        null,
        DateTimeOffset.UtcNow);
    public InteractiveSessionLifecycleSnapshot InteractiveSessionSnapshot { get; set; } = new(
        InteractiveSessionLifecycleState.None,
        null,
        DateTimeOffset.UtcNow);
    public SyncRuntimeSnapshot SyncSnapshot { get; set; } = new(SyncRuntimeState.Disabled, null);
    public int EnsureStartedCount { get; private set; }
    public int ResetCount { get; private set; }
    public int StopCount { get; private set; }
    public int DisposeCount { get; private set; }
    public Exception? StopFailure { get; set; }
    public Exception? ResetFailure { get; set; }

    public event EventHandler<BackendRuntimeStateChangedEventArgs>? StateChanged;
    public event EventHandler<SyncRuntimeStateChangedEventArgs>? SyncStateChanged;

    public Task EnsureStartedAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        EnsureStartedCount++;
        SetRuntimeState(BackendRuntimeState.Ready);
        return Task.CompletedTask;
    }

    public Task WaitUntilReadyAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.CompletedTask;
    }

    public Task<IInteractiveBackendSession> OpenInteractiveSessionAsync(
        CancellationToken cancellationToken = default) =>
        Task.FromException<IInteractiveBackendSession>(
            new NotSupportedException("This fake is used for owner lifecycle tests."));

    public Task ResetDatabaseAndRestartAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ResetCount++;
        if (ResetFailure is not null)
            return Task.FromException(ResetFailure);
        SetRuntimeState(BackendRuntimeState.Ready);
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        StopCount++;
        if (StopFailure is not null)
            return Task.FromException(StopFailure);
        SetRuntimeState(BackendRuntimeState.Stopped);
        return Task.CompletedTask;
    }

    public ValueTask DisposeAsync()
    {
        DisposeCount++;
        return ValueTask.CompletedTask;
    }

    public void SetSyncSnapshot(SyncRuntimeSnapshot snapshot)
    {
        var previous = SyncSnapshot;
        SyncSnapshot = snapshot;
        SyncStateChanged?.Invoke(this, new SyncRuntimeStateChangedEventArgs(previous, snapshot));
    }

    private void SetRuntimeState(BackendRuntimeState state)
    {
        var previous = Snapshot;
        Snapshot = new BackendRuntimeSnapshot(
            state,
            BackendRuntimeFailureKind.None,
            null,
            DateTimeOffset.UtcNow);
        StateChanged?.Invoke(this, new BackendRuntimeStateChangedEventArgs(previous, Snapshot));
    }
}
