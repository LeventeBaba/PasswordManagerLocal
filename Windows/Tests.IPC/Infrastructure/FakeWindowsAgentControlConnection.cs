using PasswordManagerLocal.Windows.Frontend.AgentConnection;
using PasswordManagerLocal.Windows.Ipc.Contracts;

namespace PasswordManagerLocal.Windows.Tests.IPC.Infrastructure;

internal sealed class FakeWindowsAgentControlConnection : IWindowsAgentControlConnection
{
    public bool IsConnected { get; set; } = true;
    public long ConnectionGeneration { get; private set; } = 1;
    public Task Completion { get; set; } = Task.CompletedTask;
    public bool EnsureConnectedResult { get; set; } = true;
    public Queue<bool> EnsureConnectedResults { get; } = new();
    public Exception? GetBackgroundFailure { get; set; }
    public Queue<Exception?> GetBackgroundFailures { get; } = new();
    public Exception? SetBackgroundFailure { get; set; }
    public CancellationTokenSource? CancelDuringSet { get; set; }
    public WindowsBackgroundSyncStateDto BackgroundState { get; set; } =
        FakeWindowsBackgroundSyncCoordinator.DisabledState();
    public int EnsureConnectedCount { get; private set; }
    public int DisconnectCount { get; private set; }
    public int GetBackgroundCount { get; private set; }
    public int SetBackgroundCount { get; private set; }
    public bool? LastRequestedEnabled { get; private set; }
    public bool MutateBeforeSetFailure { get; set; }
    public bool StallBackgroundRead { get; set; }
    public int ReloadApplicationPreferencesCount { get; private set; }
    public bool ReloadApplicationPreferencesResult { get; set; } = true;
    public Exception? ReloadApplicationPreferencesFailure { get; set; }

    public Task<bool> EnsureConnectedAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        EnsureConnectedCount++;
        var result = EnsureConnectedResults.Count == 0
            ? EnsureConnectedResult
            : EnsureConnectedResults.Dequeue();
        IsConnected = result;
        if (IsConnected)
            ConnectionGeneration++;
        return Task.FromResult(result);
    }

    public Task DisconnectAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        DisconnectCount++;
        IsConnected = false;
        return Task.CompletedTask;
    }

    public Task<bool> ConnectAsync(CancellationToken cancellationToken = default) =>
        EnsureConnectedAsync(cancellationToken);

    public Task<bool> PrepareForReplacementAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(true);
    }

    public async Task<WindowsBackgroundSyncStateDto> GetBackgroundSyncStateAsync(
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        GetBackgroundCount++;
        if (StallBackgroundRead)
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);

        var failure = GetBackgroundFailures.Count == 0
            ? GetBackgroundFailure
            : GetBackgroundFailures.Dequeue();
        if (failure is not null)
            throw failure;
        return BackgroundState;
    }

    public Task<WindowsBackgroundSyncStateDto> SetBackgroundSyncEnabledAsync(
        bool isEnabled,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        SetBackgroundCount++;
        LastRequestedEnabled = isEnabled;
        CancelDuringSet?.Cancel();
        if (MutateBeforeSetFailure)
        {
            BackgroundState = isEnabled
                ? FakeWindowsBackgroundSyncCoordinator.OperationalState()
                : FakeWindowsBackgroundSyncCoordinator.DisabledState();
        }
        if (SetBackgroundFailure is not null)
            return Task.FromException<WindowsBackgroundSyncStateDto>(SetBackgroundFailure);

        BackgroundState = isEnabled
            ? FakeWindowsBackgroundSyncCoordinator.OperationalState()
            : FakeWindowsBackgroundSyncCoordinator.DisabledState();
        return Task.FromResult(BackgroundState);
    }


    public Task<bool> ReloadApplicationPreferencesAsync(
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ReloadApplicationPreferencesCount++;
        return ReloadApplicationPreferencesFailure is null
            ? Task.FromResult(ReloadApplicationPreferencesResult)
            : Task.FromException<bool>(ReloadApplicationPreferencesFailure);
    }

    public Task<AgentStatusDto> GetAgentStatusAsync(
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(new AgentStatusDto(
            AgentState.Running,
            AgentAdmissionState.Open,
            IsUiConnected: true,
            BackendOwnedByAgent: true,
            IsBackendRunning: BackgroundState.IsRuntimeRunning,
            IsBackgroundSyncEnabled: BackgroundState.IsEnabled,
            RequiresProcessRestart: false,
            LastFailure: null,
            StartedAtUtc: DateTimeOffset.UtcNow,
            IsEndpointHostReady: true,
            HasBackgroundSyncLease: BackgroundState.IsBackgroundLeaseActive));
    }

    public Task<BackendRuntimeStatusDto> GetBackendRuntimeStatusAsync(
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(new BackendRuntimeStatusDto(
            BackgroundState.IsRuntimeRunning
                ? BackendRuntimeStatusState.Ready
                : BackendRuntimeStatusState.Stopped,
            BackendRuntimeFailureStatusKind.None,
            Failure: null,
            RequiresProcessRestart: false,
            DateTimeOffset.UtcNow));
    }

    public Task<DatabaseResetResultDto> ResetDatabaseAsync(
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(new DatabaseResetResultDto(true, false, null));
    }

    public ValueTask DisposeAsync()
    {
        IsConnected = false;
        return ValueTask.CompletedTask;
    }
}
