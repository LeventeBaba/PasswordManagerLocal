using PasswordManagerLocal.Windows.Agent.Background;
using PasswordManagerLocal.Windows.Ipc.Contracts;

namespace PasswordManagerLocal.Windows.Tests.IPC.Infrastructure;

internal sealed class FakeWindowsBackgroundSyncCoordinator : IWindowsBackgroundSyncCoordinator
{
    public WindowsBackgroundSyncStateDto State { get; set; } = DisabledState();
    public Exception? InitializeFailure { get; set; }
    public Exception? ReadFailure { get; set; }
    public Exception? SetFailure { get; set; }
    public Exception? SuspendFailure { get; set; }
    public Exception? RestoreFailure { get; set; }
    public Exception? ShutdownFailure { get; set; }
    public int InitializeCount { get; private set; }
    public int ReadCount { get; private set; }
    public int SetCount { get; private set; }
    public int SuspendCount { get; private set; }
    public int RestoreCount { get; private set; }
    public int ShutdownCount { get; private set; }
    public bool? LastRequestedEnabled { get; private set; }
    public bool? LastRestoreEnabled { get; private set; }
    public ICollection<string>? OperationLog { get; set; }

    public Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        InitializeCount++;
        OperationLog?.Add("background-initialize");
        return InitializeFailure is null
            ? Task.CompletedTask
            : Task.FromException(InitializeFailure);
    }

    public Task<WindowsBackgroundSyncStateDto> GetStateAsync(
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ReadCount++;
        return ReadFailure is null
            ? Task.FromResult(State)
            : Task.FromException<WindowsBackgroundSyncStateDto>(ReadFailure);
    }

    public Task<WindowsBackgroundSyncStateDto> SetEnabledAsync(
        bool isEnabled,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        SetCount++;
        LastRequestedEnabled = isEnabled;
        if (SetFailure is not null)
            return Task.FromException<WindowsBackgroundSyncStateDto>(SetFailure);

        State = isEnabled ? OperationalState() : DisabledState();
        return Task.FromResult(State);
    }

    public Task<bool> SuspendForDatabaseResetAsync(
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        SuspendCount++;
        OperationLog?.Add("background-suspend");
        if (SuspendFailure is not null)
            return Task.FromException<bool>(SuspendFailure);

        var enabled = State.IsEnabled;
        State = State with { IsBackgroundLeaseActive = false, IsRuntimeRunning = false };
        return Task.FromResult(enabled);
    }

    public Task RestoreAfterDatabaseResetAsync(
        bool isEnabled,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        RestoreCount++;
        LastRestoreEnabled = isEnabled;
        OperationLog?.Add("background-restore");
        if (RestoreFailure is not null)
            return Task.FromException(RestoreFailure);

        State = isEnabled ? OperationalState() : DisabledState();
        return Task.CompletedTask;
    }

    public Task ShutdownAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ShutdownCount++;
        OperationLog?.Add("background-shutdown");
        if (ShutdownFailure is not null)
            return Task.FromException(ShutdownFailure);

        State = State with
        {
            IsBackgroundLeaseActive = false,
            IsRuntimeRunning = false,
            Consistency = State.IsEnabled
                ? WindowsBackgroundSyncConsistency.Degraded
                : WindowsBackgroundSyncConsistency.Disabled
        };
        return Task.CompletedTask;
    }

    public static WindowsBackgroundSyncStateDto DisabledState() => new(
        IsEnabled: false,
        IsStartupRegistered: false,
        IsBackgroundLeaseActive: false,
        IsRuntimeRunning: false,
        IsTransitionInProgress: false,
        WindowsBackgroundSyncConsistency.Disabled,
        WindowsBackgroundSyncFailureKind.None,
        Failure: null);

    public static WindowsBackgroundSyncStateDto OperationalState() => new(
        IsEnabled: true,
        IsStartupRegistered: true,
        IsBackgroundLeaseActive: true,
        IsRuntimeRunning: true,
        IsTransitionInProgress: false,
        WindowsBackgroundSyncConsistency.Operational,
        WindowsBackgroundSyncFailureKind.None,
        Failure: null);
}
