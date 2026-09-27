using PasswordManagerLocal.Common.Contracts.Runtime;
using PasswordManagerLocal.Common.Contracts.BackgroundSync;
using PasswordManagerLocal.Common.Contracts.Endpoints;
using PasswordManagerLocal.Common.Backend.Hosting;
using PasswordManagerLocal.Common.Backend.Models;

namespace PasswordManagerLocal.Common.Tests.Fakes;

public sealed class FakeBackendRuntime : IBackendRuntime
{
    private readonly IEndpoints _endpoints;
    private bool _interactiveSessionActive;

    public FakeBackendRuntime(IEndpoints endpoints)
    {
        _endpoints = endpoints ?? throw new ArgumentNullException(nameof(endpoints));
        Snapshot = new BackendRuntimeSnapshot(
            BackendRuntimeState.NotStarted,
            BackendRuntimeFailureKind.None,
            null,
            DateTimeOffset.UtcNow);
    }

    public BackendRuntimeSnapshot Snapshot { get; private set; }
    public InteractiveSessionLifecycleSnapshot InteractiveSessionSnapshot { get; private set; } = new(
        InteractiveSessionLifecycleState.None,
        null,
        DateTimeOffset.UtcNow);
    public SyncRuntimeSnapshot SyncSnapshot { get; private set; } = new(SyncRuntimeState.Disabled, null);
    public int EnsureStartedCalls { get; private set; }
    public int WaitUntilReadyCalls { get; private set; }
    public int OpenInteractiveSessionCalls { get; private set; }
    public int ResetCalls { get; private set; }
    public int StopCalls { get; private set; }
    public int ClosedInteractiveSessionCalls { get; private set; }
    public int DisposeCalls { get; private set; }
    public Exception? StartupFailure { get; set; }
    public Exception? InteractiveSessionFailure { get; set; }
    public Exception? InteractiveSessionDisposeFailure { get; set; }
    public Exception? ResetFailure { get; set; }
    public Exception? StopFailure { get; set; }
    public Action? AfterStart { get; set; }
    public Action? BeforeReset { get; set; }
    public TaskCompletionSource? InteractiveSessionDisposeEntered { get; set; }
    public Task? InteractiveSessionDisposeRelease { get; set; }
    public TaskCompletionSource? ResetEntered { get; set; }
    public Task? ResetRelease { get; set; }

    public event EventHandler<BackendRuntimeStateChangedEventArgs>? StateChanged;
    public event EventHandler<SyncRuntimeStateChangedEventArgs>? SyncStateChanged;

    public Task EnsureStartedAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        EnsureStartedCalls++;

        if (StartupFailure is not null)
            throw StartupFailure;

        SetSnapshot(new BackendRuntimeSnapshot(
            BackendRuntimeState.Ready,
            BackendRuntimeFailureKind.None,
            null,
            DateTimeOffset.UtcNow));
        InteractiveSessionSnapshot = new InteractiveSessionLifecycleSnapshot(
            InteractiveSessionLifecycleState.None,
            null,
            DateTimeOffset.UtcNow);
        AfterStart?.Invoke();
        return Task.CompletedTask;
    }

    public async Task WaitUntilReadyAsync(CancellationToken cancellationToken = default)
    {
        WaitUntilReadyCalls++;
        await EnsureStartedAsync(cancellationToken);
    }

    public async Task<IInteractiveBackendSession> OpenInteractiveSessionAsync(
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        await WaitUntilReadyAsync(cancellationToken);

        if (_interactiveSessionActive)
            throw new InvalidOperationException("An interactive session is already active.");

        OpenInteractiveSessionCalls++;
        if (InteractiveSessionFailure is not null)
            throw InteractiveSessionFailure;

        _interactiveSessionActive = true;
        InteractiveSessionSnapshot = new InteractiveSessionLifecycleSnapshot(
            InteractiveSessionLifecycleState.Active,
            null,
            DateTimeOffset.UtcNow);
        return new FakeInteractiveBackendSession(
            _endpoints,
            async () =>
            {
                _interactiveSessionActive = false;
                ClosedInteractiveSessionCalls++;
                InteractiveSessionDisposeEntered?.TrySetResult();
                if (InteractiveSessionDisposeRelease is not null)
                    await InteractiveSessionDisposeRelease;

                InteractiveSessionSnapshot = InteractiveSessionDisposeFailure is null
                    ? new InteractiveSessionLifecycleSnapshot(
                        InteractiveSessionLifecycleState.None,
                        null,
                        DateTimeOffset.UtcNow)
                    : new InteractiveSessionLifecycleSnapshot(
                        InteractiveSessionLifecycleState.CleanupFailed,
                        InteractiveSessionDisposeFailure,
                        DateTimeOffset.UtcNow);

                if (InteractiveSessionDisposeFailure is not null)
                    throw InteractiveSessionDisposeFailure;
            });
    }

    public async Task ResetDatabaseAndRestartAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ResetCalls++;
        BeforeReset?.Invoke();
        ResetEntered?.TrySetResult();
        if (ResetRelease is not null)
            await ResetRelease.WaitAsync(cancellationToken);
        if (ResetFailure is not null)
            throw ResetFailure;
        SetSnapshot(new BackendRuntimeSnapshot(
            BackendRuntimeState.Ready,
            BackendRuntimeFailureKind.None,
            null,
            DateTimeOffset.UtcNow));
        InteractiveSessionSnapshot = new InteractiveSessionLifecycleSnapshot(
            InteractiveSessionLifecycleState.None,
            null,
            DateTimeOffset.UtcNow);
    }

    public Task StopAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        StopCalls++;
        if (StopFailure is not null)
            throw StopFailure;

        SetSnapshot(new BackendRuntimeSnapshot(
            BackendRuntimeState.Stopped,
            BackendRuntimeFailureKind.None,
            null,
            DateTimeOffset.UtcNow));
        return Task.CompletedTask;
    }

    public ValueTask DisposeAsync()
    {
        DisposeCalls++;
        return ValueTask.CompletedTask;
    }

    public void SetSnapshot(BackendRuntimeSnapshot snapshot)
    {
        var previous = Snapshot;
        Snapshot = snapshot;
        StateChanged?.Invoke(this, new BackendRuntimeStateChangedEventArgs(previous, snapshot));
    }

    public void SetSyncSnapshot(SyncRuntimeSnapshot snapshot)
    {
        var previous = SyncSnapshot;
        SyncSnapshot = snapshot;
        SyncStateChanged?.Invoke(this, new SyncRuntimeStateChangedEventArgs(previous, snapshot));
    }
}
