namespace PasswordManagerLocal.Common.Frontend.Services;

public sealed class DeviceAppPreferencesService
{
    private readonly IBackgroundSyncSettingsClient _backgroundSyncClient;
    private readonly CancellationToken _lifetime;
    private BackgroundSyncClientState _backgroundSyncState;

    public DeviceAppPreferencesService(IBackgroundSyncSettingsClient backgroundSyncClient, CancellationToken lifetime = default)
    {
        _lifetime = lifetime;
        _backgroundSyncClient = backgroundSyncClient
            ?? throw new ArgumentNullException(nameof(backgroundSyncClient));
        _backgroundSyncState = CreateUnavailableState();
    }

    public event EventHandler<DeviceAppPreferencesChangedEventArgs>? PreferencesChanged;

    public BackgroundSyncClientState BackgroundSyncState => _backgroundSyncState;

    public async Task<BackgroundSyncClientState> RefreshBackgroundSyncAsync(
        CancellationToken cancellationToken = default)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _lifetime);
        cancellationToken = linked.Token;
        cancellationToken.ThrowIfCancellationRequested();
        try
        {
            Apply(await _backgroundSyncClient.GetStateAsync(cancellationToken), false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch
        {
            Apply(CreateUnavailableState(), false);
        }

        return _backgroundSyncState;
    }

    public async Task<BackgroundSyncChangeResult> SetBackgroundSyncEnabledAsync(
        bool isEnabled,
        CancellationToken cancellationToken = default)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _lifetime);
        cancellationToken = linked.Token;
        cancellationToken.ThrowIfCancellationRequested();
        try
        {
            var result = await _backgroundSyncClient.SetEnabledAsync(isEnabled, cancellationToken);
            Apply(result.State, result.WasOutcomeUncertain);
            return result;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch
        {
            var state = CreateUnavailableState();
            Apply(state, true);
            return new BackgroundSyncChangeResult(state, WasOutcomeUncertain: true);
        }
    }

    private BackgroundSyncClientState CreateUnavailableState() => new(
        IsEnabled: false,
        IsAvailable: false,
        IsDegraded: true,
        IsTransitionInProgress: false,
        BackgroundSyncClientFailureKind.Unavailable,
        SafeMessage: null);

    private void Apply(BackgroundSyncClientState state, bool wasOutcomeUncertain)
    {
        if (_lifetime.IsCancellationRequested)
            return;
        _backgroundSyncState = state ?? throw new ArgumentNullException(nameof(state));
        PreferencesChanged?.Invoke(
            this,
            new DeviceAppPreferencesChangedEventArgs(state, wasOutcomeUncertain));
    }
}
