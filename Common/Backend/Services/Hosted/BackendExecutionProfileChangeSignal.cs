using PasswordManagerLocal.Common.Backend.Abstractions.Services;

namespace PasswordManagerLocal.Common.Backend.Services.Hosted;

internal sealed class BackendExecutionProfileChangeSignal : IDisposable
{
    private readonly IBackendExecutionProfileProvider _profileProvider;
    private readonly object _gate = new();
    private TaskCompletionSource _wake = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private long _version;
    private bool _disposed;

    public BackendExecutionProfileChangeSignal(
        IBackendExecutionProfileProvider profileProvider)
    {
        _profileProvider = profileProvider
            ?? throw new ArgumentNullException(nameof(profileProvider));
        _profileProvider.ProfileChanged += HandleProfileChanged;
    }

    public long Version
    {
        get
        {
            lock (_gate)
                return _version;
        }
    }

    public async Task<bool> WaitAsync(
        TimeSpan delay,
        long observedVersion,
        CancellationToken cancellationToken)
    {
        if (delay <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(delay));

        Task wakeTask;
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_version != observedVersion)
                return true;

            wakeTask = _wake.Task;
        }

        using var delayCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var delayTask = Task.Delay(delay, delayCancellation.Token);
        var completed = await Task.WhenAny(delayTask, wakeTask);
        if (ReferenceEquals(completed, delayTask))
        {
            await delayTask;
            return false;
        }

        delayCancellation.Cancel();
        try
        {
            await delayTask;
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
        }

        cancellationToken.ThrowIfCancellationRequested();
        return true;
    }

    public void Dispose()
    {
        TaskCompletionSource? wake = null;
        lock (_gate)
        {
            if (_disposed)
                return;

            _disposed = true;
            wake = _wake;
        }

        _profileProvider.ProfileChanged -= HandleProfileChanged;
        wake.TrySetResult();
    }

    private void HandleProfileChanged(object? sender, EventArgs args)
    {
        TaskCompletionSource wake;
        lock (_gate)
        {
            if (_disposed)
                return;

            _version++;
            wake = _wake;
            _wake = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        }

        wake.TrySetResult();
    }

}
