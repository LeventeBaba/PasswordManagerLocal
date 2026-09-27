using PasswordManagerLocal.Common.Backend.Abstractions.Services;
using PasswordManagerLocal.Common.Backend.Abstractions.State;
using PasswordManagerLocal.Common.Backend.Models;

namespace PasswordManagerLocal.Common.Tests.Fakes;

/// <summary>
/// Lightweight synchronization runtime used by production-composition enrollment tests.
/// It preserves the enrollment runtime-state contract without opening real TCP or UDP sockets.
/// </summary>
public sealed class EnrollmentAwareFakeSyncRuntimeService : ISyncRuntimeService
{
    private readonly IEnrollmentRuntimeState _enrollmentState;
    private SyncRuntimeSnapshot _snapshot = new(SyncRuntimeState.Disabled, null);

    public EnrollmentAwareFakeSyncRuntimeService(IEnrollmentRuntimeState enrollmentState) =>
        _enrollmentState = enrollmentState;

    public SyncRuntimeSnapshot Snapshot => _snapshot;

    public event EventHandler<SyncRuntimeStateChangedEventArgs>? StateChanged;

    public Task RefreshSyncEnabledAsync(CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        return Task.CompletedTask;
    }

    public Task BeginEnrollmentOnlyAsync(CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        _enrollmentState.Activate();
        Transition(SyncRuntimeState.Running);
        return Task.CompletedTask;
    }

    public Task EndEnrollmentOnlyAsync(CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        _enrollmentState.Deactivate();
        Transition(SyncRuntimeState.Disabled);
        return Task.CompletedTask;
    }

    public Task StartAsync(CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        Transition(SyncRuntimeState.Running);
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        _enrollmentState.Deactivate();
        Transition(SyncRuntimeState.Disabled);
        return Task.CompletedTask;
    }

    private void Transition(SyncRuntimeState state)
    {
        var previous = _snapshot;
        var current = new SyncRuntimeSnapshot(state, null);
        if (previous == current)
            return;

        _snapshot = current;
        StateChanged?.Invoke(this, new SyncRuntimeStateChangedEventArgs(previous, current));
    }
}
