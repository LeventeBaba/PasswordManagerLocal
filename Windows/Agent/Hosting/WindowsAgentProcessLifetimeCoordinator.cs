using PasswordManagerLocal.Windows.Agent.Background;
using PasswordManagerLocal.Windows.Ipc.Contracts;
using PasswordManagerLocal.Windows.Ipc.Coordination;
using PasswordManagerLocal.Windows.Ipc.Lifecycle;

namespace PasswordManagerLocal.Windows.Agent.Hosting;

public sealed class WindowsAgentProcessLifetimeCoordinator : IAsyncDisposable
{
    private readonly WindowsAgentLaunchMode _launchMode;
    private readonly IUiConnectionCoordinator _uiConnectionCoordinator;
    private readonly IWindowsBackgroundSyncCoordinator _backgroundSyncCoordinator;
    private readonly IProcessInstanceLockProbe _uiProcessLockProbe;
    private readonly WindowsAgentShutdownCoordinator _shutdownCoordinator;
    private readonly TimeSpan _uiRequestedRegistrationTimeout;
    private readonly TimeSpan _uiDisconnectGracePeriod;
    private readonly TimeSpan _uiPresencePollInterval;
    private readonly object _gate = new();
    private readonly CancellationTokenSource _lifetimeSource = new();
    private CancellationTokenSource? _evaluationSource;
    private Task _evaluationTask = Task.CompletedTask;
    private int _started;
    private int _disposed;

    public WindowsAgentProcessLifetimeCoordinator(
        WindowsAgentLaunchMode launchMode,
        IUiConnectionCoordinator uiConnectionCoordinator,
        IWindowsBackgroundSyncCoordinator backgroundSyncCoordinator,
        IProcessInstanceLockProbe uiProcessLockProbe,
        WindowsAgentShutdownCoordinator shutdownCoordinator,
        TimeSpan? uiRequestedRegistrationTimeout = null,
        TimeSpan? uiDisconnectGracePeriod = null,
        TimeSpan? uiPresencePollInterval = null)
    {
        if (!Enum.IsDefined(launchMode))
            throw new ArgumentOutOfRangeException(nameof(launchMode));
        _launchMode = launchMode;
        _uiConnectionCoordinator = uiConnectionCoordinator
            ?? throw new ArgumentNullException(nameof(uiConnectionCoordinator));
        _backgroundSyncCoordinator = backgroundSyncCoordinator
            ?? throw new ArgumentNullException(nameof(backgroundSyncCoordinator));
        _uiProcessLockProbe = uiProcessLockProbe
            ?? throw new ArgumentNullException(nameof(uiProcessLockProbe));
        _shutdownCoordinator = shutdownCoordinator
            ?? throw new ArgumentNullException(nameof(shutdownCoordinator));
        _uiRequestedRegistrationTimeout = uiRequestedRegistrationTimeout
            ?? TimeSpan.FromSeconds(20);
        _uiDisconnectGracePeriod = uiDisconnectGracePeriod
            ?? TimeSpan.FromMilliseconds(500);
        _uiPresencePollInterval = uiPresencePollInterval
            ?? TimeSpan.FromMilliseconds(100);
        if (_uiRequestedRegistrationTimeout <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(uiRequestedRegistrationTimeout));
        if (_uiDisconnectGracePeriod < TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(uiDisconnectGracePeriod));
        if (_uiPresencePollInterval <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(uiPresencePollInterval));
    }

    public void Start()
    {
        ThrowIfDisposed();
        if (Interlocked.Exchange(ref _started, 1) != 0)
            throw new InvalidOperationException("The Windows agent process lifetime coordinator has already started.");

        _uiConnectionCoordinator.RegistrationChanged += HandleRegistrationChanged;
        if (_uiConnectionCoordinator.Registration is null)
        {
            ScheduleEvaluation(_launchMode == WindowsAgentLaunchMode.UiRequested
                ? _uiRequestedRegistrationTimeout
                : TimeSpan.Zero);
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
            return;

        _uiConnectionCoordinator.RegistrationChanged -= HandleRegistrationChanged;
        _lifetimeSource.Cancel();
        Task evaluationTask;
        lock (_gate)
        {
            _evaluationSource?.Cancel();
            evaluationTask = _evaluationTask;
        }

        try
        {
            await evaluationTask;
        }
        catch (OperationCanceledException)
        {
        }

        lock (_gate)
        {
            _evaluationSource?.Dispose();
            _evaluationSource = null;
        }
        _lifetimeSource.Dispose();
        GC.SuppressFinalize(this);
    }

    private void HandleRegistrationChanged(
        object? sender,
        UiConnectionRegistrationChangedEventArgs args)
    {
        if (Volatile.Read(ref _disposed) != 0)
            return;

        if (args.Current is not null)
        {
            CancelEvaluation();
            return;
        }

        ScheduleEvaluation(_uiDisconnectGracePeriod);
    }

    private void ScheduleEvaluation(TimeSpan delay)
    {
        CancellationTokenSource source;
        lock (_gate)
        {
            if (Volatile.Read(ref _disposed) != 0)
                return;

            _evaluationSource?.Cancel();
            _evaluationSource?.Dispose();
            source = CancellationTokenSource.CreateLinkedTokenSource(_lifetimeSource.Token);
            _evaluationSource = source;
            _evaluationTask = EvaluateAsync(delay, source.Token);
        }
    }

    private void CancelEvaluation()
    {
        lock (_gate)
            _evaluationSource?.Cancel();
    }

    private async Task EvaluateAsync(TimeSpan delay, CancellationToken cancellationToken)
    {
        if (delay > TimeSpan.Zero)
            await Task.Delay(delay, cancellationToken);

        while (!cancellationToken.IsCancellationRequested &&
               _uiConnectionCoordinator.Registration is null)
        {
            try
            {
                var backgroundState = await _backgroundSyncCoordinator.GetStateAsync(
                    cancellationToken);
                if (backgroundState.IsTransitionInProgress ||
                    backgroundState.Consistency == WindowsBackgroundSyncConsistency.Unavailable)
                {
                    await Task.Delay(_uiPresencePollInterval, cancellationToken);
                    continue;
                }
                if (backgroundState.IsEnabled || backgroundState.IsBackgroundLeaseActive)
                    return;

                var uiPresence = _uiProcessLockProbe.Probe();
                if (uiPresence == ProcessInstanceLockProbeResult.Free)
                {
                    _shutdownCoordinator.RequestShutdown(
                        WindowsAgentShutdownReason.NoUiAndBackgroundDisabled);
                    return;
                }

                await Task.Delay(_uiPresencePollInterval, cancellationToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                return;
            }
            catch
            {
                await Task.Delay(_uiPresencePollInterval, cancellationToken);
            }
        }
    }

    private void ThrowIfDisposed()
    {
        if (Volatile.Read(ref _disposed) != 0)
            throw new ObjectDisposedException(nameof(WindowsAgentProcessLifetimeCoordinator));
    }
}
