using PasswordManagerLocal.Windows.Frontend.AgentConnection;
using PasswordManagerLocal.Windows.Ipc.Contracts;

namespace PasswordManagerLocal.Windows.Tests.IPC.Infrastructure;

internal sealed class FakeWindowsAgentRegisteredConnection : IWindowsAgentRegisteredConnection
{
    private readonly TaskCompletionSource _completion = new(
        TaskCreationOptions.RunContinuationsAsynchronously);

    public bool IsConnected { get; set; } = true;
    public int? AgentProcessId { get; set; } = 6000;
    public Task Completion => _completion.Task;
    public int DisposeCount { get; private set; }
    public BackendRuntimeStatusDto BackendStatus { get; set; } = new(
        BackendRuntimeStatusState.Ready,
        BackendRuntimeFailureStatusKind.None,
        Failure: null,
        RequiresProcessRestart: false,
        DateTimeOffset.UtcNow);
    public AgentStatusDto AgentStatus { get; set; } = new(
        AgentState.Running,
        AgentAdmissionState.Open,
        IsUiConnected: true,
        BackendOwnedByAgent: true,
        IsBackendRunning: true,
        IsBackgroundSyncEnabled: false,
        RequiresProcessRestart: false,
        LastFailure: null,
        StartedAtUtc: DateTimeOffset.UtcNow,
        IsEndpointHostReady: true);
    public WindowsBackgroundSyncStateDto BackgroundSyncState { get; set; } =
        FakeWindowsBackgroundSyncCoordinator.DisabledState();
    public Exception? BackgroundSyncSetFailure { get; set; }
    public int BackgroundSyncSetCount { get; private set; }
    public bool? LastBackgroundSyncEnabled { get; private set; }
    public int ReloadApplicationPreferencesCount { get; private set; }
    public RequestAcceptedDto ReloadApplicationPreferencesResult { get; set; } = new(true);

    public DatabaseResetResultDto DatabaseResetResult { get; set; } = new(
        Completed: true,
        RequiresProcessRestart: false,
        SafeMessage: null);

    public Task<BackendRuntimeStatusDto> GetBackendRuntimeStatusAsync(
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(BackendStatus);
    }

    public Task<AgentStatusDto> GetAgentStatusAsync(
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(AgentStatus);
    }

    public Task<WindowsBackgroundSyncStateDto> GetBackgroundSyncStateAsync(
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(BackgroundSyncState);
    }

    public Task<WindowsBackgroundSyncStateDto> SetBackgroundSyncEnabledAsync(
        SetBackgroundSyncEnabledRequestDto request,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        BackgroundSyncSetCount++;
        LastBackgroundSyncEnabled = request.IsEnabled;
        if (BackgroundSyncSetFailure is not null)
            return Task.FromException<WindowsBackgroundSyncStateDto>(BackgroundSyncSetFailure);

        BackgroundSyncState = request.IsEnabled
            ? FakeWindowsBackgroundSyncCoordinator.OperationalState()
            : FakeWindowsBackgroundSyncCoordinator.DisabledState();
        return Task.FromResult(BackgroundSyncState);
    }


    public Task<RequestAcceptedDto> ReloadApplicationPreferencesAsync(
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ReloadApplicationPreferencesCount++;
        return Task.FromResult(ReloadApplicationPreferencesResult);
    }

    public Task<DatabaseResetResultDto> ResetDatabaseAsync(
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(DatabaseResetResult);
    }

    public void Disconnect(Exception? failure = null)
    {
        IsConnected = false;
        if (failure is null)
            _completion.TrySetResult();
        else
            _completion.TrySetException(failure);
    }

    public ValueTask DisposeAsync()
    {
        DisposeCount++;
        IsConnected = false;
        _completion.TrySetResult();
        return ValueTask.CompletedTask;
    }
}
