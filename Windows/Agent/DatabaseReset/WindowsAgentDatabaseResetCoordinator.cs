using PasswordManagerLocal.Windows.Agent.Localization;
using PasswordManagerLocal.Common.Backend.Hosting;
using PasswordManagerLocal.Common.Contracts.Runtime;
using PasswordManagerLocal.Common.Contracts.BackgroundSync;
using PasswordManagerLocal.Windows.Agent.Backend;
using PasswordManagerLocal.Windows.Agent.Background;
using PasswordManagerLocal.Windows.Agent.Lifecycle;
using PasswordManagerLocal.Windows.Agent.Endpoint;
using PasswordManagerLocal.Windows.Agent.Hosting;
using PasswordManagerLocal.Windows.Ipc.Contracts;

namespace PasswordManagerLocal.Windows.Agent.DatabaseReset;

public sealed class WindowsAgentDatabaseResetCoordinator : IWindowsAgentDatabaseResetCoordinator
{
    private readonly IWindowsAgentEndpointHost _endpointHost;
    private readonly IWindowsAgentBackendRuntimeOwner _backendOwner;
    private readonly WindowsAgentShutdownCoordinator _shutdownCoordinator;
    private readonly IWindowsBackgroundSyncCoordinator _backgroundSyncCoordinator;
    private readonly WindowsAgentLifecycleTransitionCoordinator _lifecycleTransitions;
    private readonly IAgentLocalizer _localizer;
    private readonly SemaphoreSlim _resetGate = new(1, 1);
    private int _resetting;

    public WindowsAgentDatabaseResetCoordinator(
        IWindowsAgentEndpointHost endpointHost,
        IWindowsAgentBackendRuntimeOwner backendOwner,
        WindowsAgentShutdownCoordinator shutdownCoordinator,
        IWindowsBackgroundSyncCoordinator backgroundSyncCoordinator,
        WindowsAgentLifecycleTransitionCoordinator lifecycleTransitions,
        IAgentLocalizer localizer)
    {
        _endpointHost = endpointHost ?? throw new ArgumentNullException(nameof(endpointHost));
        _backendOwner = backendOwner ?? throw new ArgumentNullException(nameof(backendOwner));
        _shutdownCoordinator = shutdownCoordinator ?? throw new ArgumentNullException(nameof(shutdownCoordinator));
        _backgroundSyncCoordinator = backgroundSyncCoordinator
            ?? throw new ArgumentNullException(nameof(backgroundSyncCoordinator));
        _lifecycleTransitions = lifecycleTransitions
            ?? throw new ArgumentNullException(nameof(lifecycleTransitions));
        _localizer = localizer ?? throw new ArgumentNullException(nameof(localizer));
    }

    public bool IsResetting => Volatile.Read(ref _resetting) != 0;

    public async Task<DatabaseResetResultDto> ResetAsync(
        CancellationToken cancellationToken = default)
    {
        if (!await _resetGate.WaitAsync(TimeSpan.Zero, cancellationToken))
        {
            return new DatabaseResetResultDto(
                Completed: false,
                RequiresProcessRestart: false,
                SafeMessage: _localizer.GetString(AgentLocalizationKeys.DatabaseResetInProgress));
        }

        WindowsAgentLifecycleTransitionLease? transition = null;
        Interlocked.Exchange(ref _resetting, 1);
        try
        {
            transition = await _lifecycleTransitions.EnterAsync(
                WindowsAgentLifecycleTransitionState.ResettingDatabase,
                cancellationToken);
            var snapshot = _backendOwner.Snapshot;
            if (snapshot.RequiresProcessRestart || snapshot.IsResetting ||
                snapshot.Runtime.State != BackendRuntimeState.Failed ||
                snapshot.Runtime.FailureKind != BackendRuntimeFailureKind.DatabaseCompatibility)
            {
                return new DatabaseResetResultDto(
                    Completed: false,
                    RequiresProcessRestart: snapshot.RequiresProcessRestart,
                    SafeMessage: snapshot.RequiresProcessRestart
                        ? _localizer.GetString(AgentLocalizationKeys.DatabaseRestartRequired)
                        : _localizer.GetString(AgentLocalizationKeys.DatabaseCompatibilityRequired));
            }

            cancellationToken.ThrowIfCancellationRequested();
            // Once endpoint admission closes, reset completion is independent of the requesting UI connection.
            var backgroundEnabled = await _backgroundSyncCoordinator.SuspendForDatabaseResetAsync(
                CancellationToken.None);
            await _endpointHost.StopAsync(CancellationToken.None);
            await _backendOwner.ResetDatabaseAsync(CancellationToken.None);
            await _backgroundSyncCoordinator.RestoreAfterDatabaseResetAsync(
                backgroundEnabled,
                CancellationToken.None);
            await _endpointHost.StartAsync(CancellationToken.None);
            return new DatabaseResetResultDto(
                Completed: true,
                RequiresProcessRestart: false,
                SafeMessage: null);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            _backendOwner.RequireProcessRestart(exception);
            _shutdownCoordinator.RequestShutdown(WindowsAgentShutdownReason.RestartRequired);
            return new DatabaseResetResultDto(
                Completed: false,
                RequiresProcessRestart: true,
                SafeMessage: _localizer.GetString(AgentLocalizationKeys.DatabaseResetFailed));
        }
        finally
        {
            if (transition is not null)
                await transition.DisposeAsync();
            Interlocked.Exchange(ref _resetting, 0);
            _resetGate.Release();
        }
    }
}
