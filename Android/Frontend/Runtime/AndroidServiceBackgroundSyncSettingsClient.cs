using PasswordManagerLocal.Android.Runtime;
using PasswordManagerLocal.Common.Frontend.Services;

namespace PasswordManagerLocal.Android.Frontend;

public sealed class AndroidServiceBackgroundSyncSettingsClient : IBackgroundSyncSettingsClient
{
    private readonly PasswordManagerBackgroundService _service;
    private readonly AndroidServiceFrontendBackendClient _backendClient;
    private readonly Func<bool> _isAttachmentDisposed;

    public AndroidServiceBackgroundSyncSettingsClient(
        PasswordManagerBackgroundService service,
        AndroidServiceFrontendBackendClient backendClient,
        Func<bool> isAttachmentDisposed)
    {
        _service = service ?? throw new ArgumentNullException(nameof(service));
        _backendClient = backendClient ?? throw new ArgumentNullException(nameof(backendClient));
        _isAttachmentDisposed = isAttachmentDisposed
            ?? throw new ArgumentNullException(nameof(isAttachmentDisposed));
    }

    public async Task<BackgroundSyncClientState> GetStateAsync(
        CancellationToken cancellationToken = default)
    {
        ThrowIfDetached();
        return Map(await _service.GetBackgroundStateAsync(_backendClient, cancellationToken));
    }

    public async Task<BackgroundSyncChangeResult> SetEnabledAsync(
        bool isEnabled,
        CancellationToken cancellationToken = default)
    {
        ThrowIfDetached();
        var state = await _service.SetBackgroundEnabledAsync(
            _backendClient,
            isEnabled,
            cancellationToken);
        return new BackgroundSyncChangeResult(
            Map(state),
            WasOutcomeUncertain: state.FailureKind == AndroidBackgroundSyncFailureKind.Rollback);
    }

    private void ThrowIfDetached()
    {
        if (_isAttachmentDisposed())
            throw new ObjectDisposedException(nameof(AndroidServiceBackgroundSyncSettingsClient));
    }

    private BackgroundSyncClientState Map(AndroidBackgroundSyncServiceState state) => new(
        state.IsEnabled,
        state.IsAvailable,
        state.IsDegraded,
        state.IsTransitionInProgress,
        MapFailure(state.FailureKind),
        state.SafeMessage);

    private BackgroundSyncClientFailureKind MapFailure(
        AndroidBackgroundSyncFailureKind failureKind) => failureKind switch
    {
        AndroidBackgroundSyncFailureKind.None => BackgroundSyncClientFailureKind.None,
        AndroidBackgroundSyncFailureKind.SettingRead => BackgroundSyncClientFailureKind.Unavailable,
        AndroidBackgroundSyncFailureKind.SettingPersistence => BackgroundSyncClientFailureKind.SettingPersistence,
        AndroidBackgroundSyncFailureKind.ForegroundService => BackgroundSyncClientFailureKind.StartupRegistration,
        AndroidBackgroundSyncFailureKind.RuntimeComposition => BackgroundSyncClientFailureKind.Runtime,
        AndroidBackgroundSyncFailureKind.RuntimeLease => BackgroundSyncClientFailureKind.Runtime,
        AndroidBackgroundSyncFailureKind.SecureStorageDeferred => BackgroundSyncClientFailureKind.Runtime,
        AndroidBackgroundSyncFailureKind.Rollback => BackgroundSyncClientFailureKind.Rollback,
        AndroidBackgroundSyncFailureKind.Shutdown => BackgroundSyncClientFailureKind.Runtime,
        AndroidBackgroundSyncFailureKind.AttachmentAuthority => BackgroundSyncClientFailureKind.Unavailable,
        AndroidBackgroundSyncFailureKind.RuntimeUnsafe => BackgroundSyncClientFailureKind.Runtime,
        _ => BackgroundSyncClientFailureKind.Unavailable
    };
}
