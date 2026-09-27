using PasswordManagerLocal.Common.Backend.Hosting;
using PasswordManagerLocal.Common.Backend.Models;
using PasswordManagerLocal.Common.Contracts.Runtime;
using PasswordManagerLocal.Common.Contracts.BackgroundSync;
using PasswordManagerLocal.Windows.Agent.Backend;

namespace PasswordManagerLocal.Windows.Tests.IPC.Infrastructure;

internal sealed class FakeWindowsAgentBackendRuntimeOwner : IWindowsAgentBackendRuntimeOwner
{
    public WindowsAgentBackendOwnerSnapshot Snapshot { get; set; } = CreateSnapshot();
    public Exception? StartFailure { get; set; }
    public Exception? StopFailure { get; set; }
    public Exception? ResetFailure { get; set; }
    public Exception? BackgroundLeaseFailure { get; set; }
    public Exception? BackgroundLeaseDisposeFailure { get; set; }
    public Exception? DisposeFailure { get; set; }
    public WindowsAgentBackendOwnerState StateAfterStart { get; set; } =
        WindowsAgentBackendOwnerState.Ready;
    public int StartCount { get; private set; }
    public int StopCount { get; private set; }
    public int ResetCount { get; private set; }
    public int DisposeCount { get; private set; }
    public int RequireRestartCount { get; private set; }
    public ICollection<string>? OperationLog { get; set; }
    public Func<CancellationToken, Task<AgentInteractiveBackendBinding>>? InteractiveBindingFactory { get; set; }
    public int OpenBindingCount { get; private set; }
    public int BackgroundLeaseAcquireCount { get; private set; }
    public FakeAgentBackendRuntimeLease? LastBackgroundLease { get; private set; }

    public event EventHandler? StateChanged;

    public Task StartAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        StartCount++;
        OperationLog?.Add("backend-start");
        if (StartFailure is not null)
            return Task.FromException(StartFailure);
        Snapshot = Snapshot with
        {
            State = StateAfterStart,
            ChangedAtUtc = DateTimeOffset.UtcNow
        };
        StateChanged?.Invoke(this, EventArgs.Empty);
        return Task.CompletedTask;
    }

    public Task<IBackendRuntimeLease> AcquireBackgroundSyncLeaseAsync(
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        BackgroundLeaseAcquireCount++;
        if (BackgroundLeaseFailure is not null)
            return Task.FromException<IBackendRuntimeLease>(BackgroundLeaseFailure);

        LastBackgroundLease = new FakeAgentBackendRuntimeLease(
            BackendLifetimeReason.BackgroundSync,
            OperationLog,
            () =>
            {
                if (BackgroundLeaseDisposeFailure is not null)
                    return ValueTask.FromException(BackgroundLeaseDisposeFailure);

                var remainingReasons = Snapshot.ActiveReasons & ~BackendLifetimeReason.BackgroundSync;
                Snapshot = Snapshot with
                {
                    Runtime = Snapshot.Runtime with
                    {
                        State = remainingReasons == BackendLifetimeReason.None
                            ? BackendRuntimeState.Stopped
                            : BackendRuntimeState.Ready
                    },
                    ActiveReasons = remainingReasons,
                    ChangedAtUtc = DateTimeOffset.UtcNow
                };
                StateChanged?.Invoke(this, EventArgs.Empty);
                return ValueTask.CompletedTask;
            });
        Snapshot = Snapshot with
        {
            Runtime = Snapshot.Runtime with { State = BackendRuntimeState.Ready },
            ActiveReasons = Snapshot.ActiveReasons | BackendLifetimeReason.BackgroundSync,
            ChangedAtUtc = DateTimeOffset.UtcNow
        };
        StateChanged?.Invoke(this, EventArgs.Empty);
        return Task.FromResult<IBackendRuntimeLease>(LastBackgroundLease);
    }

    public Task<AgentInteractiveBackendBinding> OpenInteractiveBindingAsync(
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        OpenBindingCount++;
        return InteractiveBindingFactory is null
            ? Task.FromException<AgentInteractiveBackendBinding>(
                new NotSupportedException("The fake owner does not create interactive bindings."))
            : InteractiveBindingFactory(cancellationToken);
    }

    public Task ResetDatabaseAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ResetCount++;
        OperationLog?.Add("backend-reset");
        return ResetFailure is null
            ? Task.CompletedTask
            : Task.FromException(ResetFailure);
    }

    public Task StopAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        StopCount++;
        OperationLog?.Add("backend-stop");
        if (StopFailure is not null)
            return Task.FromException(StopFailure);
        Snapshot = Snapshot with
        {
            State = WindowsAgentBackendOwnerState.Stopped,
            ChangedAtUtc = DateTimeOffset.UtcNow
        };
        StateChanged?.Invoke(this, EventArgs.Empty);
        return Task.CompletedTask;
    }

    public void PublishSnapshot(WindowsAgentBackendOwnerSnapshot snapshot)
    {
        Snapshot = snapshot ?? throw new ArgumentNullException(nameof(snapshot));
        StateChanged?.Invoke(this, EventArgs.Empty);
    }

    public void RequireProcessRestart(Exception failure)
    {
        ArgumentNullException.ThrowIfNull(failure);
        RequireRestartCount++;
        Snapshot = Snapshot with
        {
            State = WindowsAgentBackendOwnerState.RestartRequired,
            RequiresProcessRestart = true,
            Failure = failure,
            ChangedAtUtc = DateTimeOffset.UtcNow
        };
        StateChanged?.Invoke(this, EventArgs.Empty);
    }

    public ValueTask DisposeAsync()
    {
        DisposeCount++;
        OperationLog?.Add("backend-dispose");
        return DisposeFailure is null
            ? ValueTask.CompletedTask
            : ValueTask.FromException(DisposeFailure);
    }

    public static WindowsAgentBackendOwnerSnapshot CreateSnapshot(
        WindowsAgentBackendOwnerState ownerState = WindowsAgentBackendOwnerState.Ready,
        BackendRuntimeState runtimeState = BackendRuntimeState.NotStarted,
        BackendRuntimeFailureKind runtimeFailureKind = BackendRuntimeFailureKind.None,
        InteractiveSessionLifecycleState interactiveState = InteractiveSessionLifecycleState.None,
        SyncRuntimeState syncState = SyncRuntimeState.Disabled,
        BackendLifetimeReason activeReasons = BackendLifetimeReason.None,
        bool requiresProcessRestart = false,
        bool isResetting = false,
        Exception? failure = null)
    {
        var now = DateTimeOffset.UtcNow;
        return new WindowsAgentBackendOwnerSnapshot(
            ownerState,
            new BackendRuntimeSnapshot(runtimeState, runtimeFailureKind, failure, now),
            new InteractiveSessionLifecycleSnapshot(interactiveState, failure, now),
            new SyncRuntimeSnapshot(syncState, failure),
            activeReasons,
            requiresProcessRestart,
            isResetting,
            failure,
            now);
    }
}
