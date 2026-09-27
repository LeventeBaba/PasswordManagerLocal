using PasswordManagerLocal.Common.Backend.Abstractions.Services;
using PasswordManagerLocal.Common.Backend.Models;

namespace PasswordManagerLocal.Common.Tests.Fakes;

public sealed class FakeSyncRuntimeService : ISyncRuntimeService
{
    private SyncRuntimeSnapshot _snapshot = new(SyncRuntimeState.Disabled, null);

    public int RefreshSyncEnabledCalls { get; private set; }
    public int BeginEnrollmentOnlyCalls { get; private set; }
    public int EndEnrollmentOnlyCalls { get; private set; }
    public int StartCalls { get; private set; }
    public int StopCalls { get; private set; }
    public Exception? RefreshFailure { get; set; }
    public TaskCompletionSource<bool>? BeginEnrollmentOnlyGate { get; set; }

    public SyncRuntimeSnapshot Snapshot => _snapshot;

    public event EventHandler<SyncRuntimeStateChangedEventArgs>? StateChanged;

    public Task RefreshSyncEnabledAsync(CancellationToken ct = default)
    {
        RefreshSyncEnabledCalls++;
        if (RefreshFailure is null)
            return Task.CompletedTask;

        Transition(SyncRuntimeState.Degraded, RefreshFailure);
        return Task.FromException(RefreshFailure);
    }

    public async Task BeginEnrollmentOnlyAsync(CancellationToken ct = default)
    {
        BeginEnrollmentOnlyCalls++;
        if (BeginEnrollmentOnlyGate is not null)
            await BeginEnrollmentOnlyGate.Task.WaitAsync(ct);
        Transition(SyncRuntimeState.Running);
    }

    public Task EndEnrollmentOnlyAsync(CancellationToken ct = default)
    {
        EndEnrollmentOnlyCalls++;
        Transition(SyncRuntimeState.Disabled);
        return Task.CompletedTask;
    }

    public Task StartAsync(CancellationToken ct = default)
    {
        StartCalls++;
        Transition(SyncRuntimeState.Running);
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken ct = default)
    {
        StopCalls++;
        Transition(SyncRuntimeState.Disabled);
        return Task.CompletedTask;
    }

    public void SetDegraded(Exception failure) => Transition(SyncRuntimeState.Degraded, failure);

    private void Transition(SyncRuntimeState state, Exception? failure = null)
    {
        var previous = _snapshot;
        var current = new SyncRuntimeSnapshot(state, failure);
        if (previous == current)
            return;

        _snapshot = current;
        StateChanged?.Invoke(this, new SyncRuntimeStateChangedEventArgs(previous, current));
    }
}
