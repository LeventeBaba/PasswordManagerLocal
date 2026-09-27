using PasswordManagerLocal.Common.Contracts.Runtime;
using PasswordManagerLocal.Common.Contracts.BackgroundSync;

namespace PasswordManagerLocal.Common.Frontend.Services;

public sealed class StoreBackgroundSyncSettingsClient : IBackgroundSyncSettingsClient
{
    private readonly IBackgroundSyncSettingsStore _store;

    public StoreBackgroundSyncSettingsClient(IBackgroundSyncSettingsStore store)
    {
        _store = store ?? throw new ArgumentNullException(nameof(store));
    }

    public async Task<BackgroundSyncClientState> GetStateAsync(
        CancellationToken cancellationToken = default)
    {
        var settings = await _store.ReadAsync(cancellationToken);
        return new BackgroundSyncClientState(
            settings.IsEnabled,
            IsAvailable: true,
            IsDegraded: false,
            IsTransitionInProgress: false,
            BackgroundSyncClientFailureKind.None,
            SafeMessage: null);
    }

    public async Task<BackgroundSyncChangeResult> SetEnabledAsync(
        bool isEnabled,
        CancellationToken cancellationToken = default)
    {
        await _store.WriteAsync(new BackgroundSyncSettings(isEnabled), cancellationToken);
        return new BackgroundSyncChangeResult(
            new BackgroundSyncClientState(
                isEnabled,
                IsAvailable: true,
                IsDegraded: false,
                IsTransitionInProgress: false,
                BackgroundSyncClientFailureKind.None,
                SafeMessage: null),
            WasOutcomeUncertain: false);
    }
}
