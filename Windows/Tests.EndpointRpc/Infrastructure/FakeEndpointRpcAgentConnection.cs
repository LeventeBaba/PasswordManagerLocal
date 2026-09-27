using PasswordManagerLocal.Windows.EndpointRpc.Client;
using PasswordManagerLocal.Windows.Ipc.Contracts;

namespace PasswordManagerLocal.Windows.Tests.EndpointRpc.Infrastructure;

public sealed class FakeEndpointRpcAgentConnection : IEndpointRpcAgentConnection
{
    private readonly Queue<bool> _ensureResults = new();
    private TaskCompletionSource _completion = CreateCompletion();

    public bool IsConnected { get; private set; } = true;
    public long ConnectionGeneration { get; private set; } = 1;
    public Task Completion => _completion.Task;
    public int EnsureCount { get; private set; }
    public int DisconnectCount { get; private set; }
    public int PrepareReplacementCount { get; private set; }
    public int ResetCount { get; private set; }
    public int DisposeCount { get; private set; }
    public bool DisconnectOnReset { get; set; }
    public TaskCompletionSource<DatabaseResetResultDto>? ResetCompletion { get; set; }
    public TaskCompletionSource<bool>? ReplacementPreparationCompletion { get; set; }
    public BackendRuntimeStatusDto BackendStatus { get; set; } = new(
        BackendRuntimeStatusState.Ready,
        BackendRuntimeFailureStatusKind.None,
        null,
        false,
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
    public DatabaseResetResultDto ResetResult { get; set; } = new(true, false, null);

    public void EnqueueEnsureResult(bool result) => _ensureResults.Enqueue(result);

    public TaskCompletionSource ReplaceConnectionWithoutCompletingPrevious()
    {
        var previous = _completion;
        IsConnected = true;
        ConnectionGeneration++;
        _completion = CreateCompletion();
        return previous;
    }

    public Task<bool> EnsureConnectedAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        EnsureCount++;
        var result = _ensureResults.Count == 0 ? true : _ensureResults.Dequeue();
        if (result && !IsConnected)
        {
            IsConnected = true;
            ConnectionGeneration++;
            _completion = CreateCompletion();
        }
        return Task.FromResult(result);
    }

    public Task DisconnectAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        DisconnectCount++;
        Disconnect();
        return Task.CompletedTask;
    }

    public Task<bool> PrepareForReplacementAsync(
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        PrepareReplacementCount++;
        Disconnect();
        return ReplacementPreparationCompletion is null
            ? Task.FromResult(true)
            : ReplacementPreparationCompletion.Task.WaitAsync(cancellationToken);
    }

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

    public Task<DatabaseResetResultDto> ResetDatabaseAsync(
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ResetCount++;
        if (DisconnectOnReset)
            Disconnect();
        return ResetCompletion is null
            ? Task.FromResult(ResetResult)
            : ResetCompletion.Task.WaitAsync(cancellationToken);
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

    private static TaskCompletionSource CreateCompletion() =>
        new(TaskCreationOptions.RunContinuationsAsynchronously);
}
