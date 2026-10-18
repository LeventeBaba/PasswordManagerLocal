using PasswordManagerLocal.Common.Frontend.Services;
using PasswordManagerLocal.Common.Frontend.ViewModels;
using PasswordManagerLocal.Common.Frontend.Views;
using PasswordManagerLocal.Common.Contracts.Runtime;

namespace PasswordManagerLocal.Common.Frontend;

/// <summary>
/// Owns one frontend graph. Construct, initialize and begin disposal on the UI thread.
/// The Android host transfers its interactive client here; the service retains background ownership.
/// </summary>
public sealed class FrontendUiSession : IAsyncDisposable, IFrontendChangeHintSource
{
    private readonly FrontendApplicationContext _context;
    private readonly bool _ownsBackendClient;
    private readonly object _disposalGate = new();
    private Task? _initialization;
    private Task? _disposal;
    private readonly FrontendChangeNotificationForwarder? _changeNotificationForwarder;
    private FrontendRefreshCoordinator? _refreshCoordinator;
    private UiPreferencesService? _uiPreferences;
    private int _disposed;

    public FrontendUiSession(MainView view, FrontendApplicationContext context, bool ownsBackendClient)
    {
        View = view ?? throw new ArgumentNullException(nameof(view));
        _context = context ?? throw new ArgumentNullException(nameof(context));
        _ownsBackendClient = ownsBackendClient;
        if (_context.BackendClient is IFrontendChangeNotificationClient or IFrontendChangeStreamClient)
        {
            _changeNotificationForwarder = new FrontendChangeNotificationForwarder(
                _context.BackendClient,
                _context.BackendClient as IFrontendChangeNotificationClient,
                GetCurrentUserId,
                () => !IsDisposed,
                _context.PlatformServices.LifetimeToken);
            _changeNotificationForwarder.ChangeHintReceived += HandleChangeHintReceived;
        }
    }

    public Guid Id { get; } = Guid.NewGuid();
    public MainView View { get; }
    public MainViewModel? ViewModel { get; private set; }
    public AuthSessionRegistry Authentication { get; } = new();
    public FrontendPlatformServices PlatformServices => _context.PlatformServices;
    public UiPreferencesService? UiPreferences => _uiPreferences;
    public bool IsDisposed => Volatile.Read(ref _disposed) != 0;
    public FrontendChangeStreamStart? CurrentChangePosition =>
        _changeNotificationForwarder?.CurrentPosition;

    public FrontendRefreshCoordinator? RefreshCoordinator => _refreshCoordinator;

    /// <summary>Metadata-only change hints delivered only during this UI session.</summary>
    public event Action<FrontendChangeStreamDelivery>? ChangeHintReceived;

    public Task InitializeAsync()
    {
        if (IsDisposed)
            return Task.CompletedTask;
        return _initialization ??= InitializeCoreAsync();
    }

    private async Task InitializeCoreAsync()
    {
        var token = PlatformServices.LifetimeToken;
        try
        {
            // Load preferences asynchronously before constructing VMs; lifecycle callbacks
            // never synchronously wait for either storage or backend readiness.
            var preferences = await _context.ApplicationPreferencesStore.ReadAsync(token);
            token.ThrowIfCancellationRequested();
            _uiPreferences = await UiPreferencesService.CreateAsync(
                _context.ApplicationPreferencesStore,
                _context.ApplicationPreferencesChangeNotifier,
                PlatformServices,
                preferences,
                cancellationToken: token);
            token.ThrowIfCancellationRequested();
            ViewModel = new MainViewModel(
                new DeferredEndpoints(_context.BackendClient, token),
                _context.BackendClient,
                _context.BackgroundSyncSettingsClient,
                _uiPreferences,
                Authentication,
                token);
            View.DataContext = ViewModel;
            ViewModel.RefreshNavigationChanged += HandleRefreshNavigationChanged;
            ViewModel.RefreshPageStateChanged += HandleRefreshPageStateChanged;
            ViewModel.RefreshAuthenticationChanged += HandleRefreshAuthenticationChanged;
            _refreshCoordinator = new FrontendRefreshCoordinator(
                this,
                ViewModel,
                new AvaloniaFrontendRefreshDispatcher(),
                sessionLifetime: token);
            // Register after the coordinator is listening so even a change arriving during
            // authoritative startup is covered by a fresh watermark/refresh marker.
            _changeNotificationForwarder?.ReconcileOnResume();
            await ViewModel.InitializeAsync();
            _refreshCoordinator.Start(ViewModel.GetVisiblePages(), _changeNotificationForwarder?.CurrentPosition);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
    }

    public async Task ResumeAsync()
    {
        var initializedBeforeResume = _initialization?.IsCompleted == true;
        await InitializeAsync();
        if (IsDisposed || ViewModel is not { } model)
            return;

        model.RefreshPlatformAnimationAvailability();
        if (initializedBeforeResume && !model.IsAuthenticated)
            await model.InitializeAsync();

        if (!IsDisposed)
        {
            _changeNotificationForwarder?.ReconcileOnResume();
            _refreshCoordinator?.RequestAuthoritativeRefresh();
        }
    }

    public ValueTask DisposeAsync()
    {
        lock (_disposalGate)
        {
            if (_disposal is not null)
                return new ValueTask(_disposal);
            if (Interlocked.Exchange(ref _disposed, 1) != 0)
                return ValueTask.CompletedTask;

            // Revoke UI admission and detach native/view references synchronously, before
            // awaiting service cleanup. A replacement Activity never shares these adapters.
            _refreshCoordinator?.Deactivate();
            _changeNotificationForwarder?.Deactivate();
            ChangeHintReceived = null;
            if (ViewModel is { } model)
            {
                model.RefreshNavigationChanged -= HandleRefreshNavigationChanged;
                model.RefreshPageStateChanged -= HandleRefreshPageStateChanged;
                model.RefreshAuthenticationChanged -= HandleRefreshAuthenticationChanged;
            }
            _uiPreferences?.Deactivate();
            PlatformServices.Deactivate();
            View.Dispose();
            ViewModel?.Dispose();
            Authentication.Dispose();
            _disposal = CompleteDisposalAsync();
            return new ValueTask(_disposal);
        }
    }

    private async Task CompleteDisposalAsync()
    {
        try
        {
            // Release localization/theme ownership before potentially slower backend-client
            // teardown. Android's process Application can outlive every Activity, so the
            // selected dictionary must not remain rooted after the last UI session closes.
            if (_uiPreferences is { } preferences)
            {
                await preferences.DisposeAsync();
                _uiPreferences = null;
            }
            if (_refreshCoordinator is not null)
                await _refreshCoordinator.DisposeAsync();
            if (_changeNotificationForwarder is not null)
                await Task.Run(async () => await _changeNotificationForwarder.DisposeAsync());
            if (_ownsBackendClient)
                await Task.Run(async () => await _context.BackendClient.DisposeAsync());
        }
        finally
        {
            try
            {
                if (_initialization is not null)
                    await _initialization;
            }
            finally
            {
                // Initialization may have published preferences after the early check but
                // before observing the canceled platform lifetime.
                if (_uiPreferences is not null)
                    await _uiPreferences.DisposeAsync();
                _uiPreferences = null;
                ViewModel = null;
                PlatformServices.Dispose();
            }
        }
    }

    private Guid? GetCurrentUserId()
    {
        if (ViewModel?.IsAuthenticated != true)
            return null;

        var token = Authentication.CurrentUserToken;
        var userId = Authentication.GetSession(token)?.UserId;
        return userId is { } value && value != Guid.Empty ? value : null;
    }

    private void HandleChangeHintReceived(FrontendChangeStreamDelivery delivery)
    {
        var handlers = ChangeHintReceived;
        if (handlers is null)
            return;

        foreach (Action<FrontendChangeStreamDelivery> handler in handlers.GetInvocationList())
        {
            if (IsDisposed)
                return;
            try { handler(delivery); }
            catch
            {
                // Observers cannot disrupt UI-session ownership or backend notifications.
            }
        }
    }

    private void HandleRefreshNavigationChanged(object? sender, EventArgs args) =>
        _refreshCoordinator?.OnNavigationChanged(ViewModel?.GetVisiblePages() ?? []);

    private void HandleRefreshPageStateChanged(object? sender, EventArgs args) =>
        _refreshCoordinator?.OnPageStateChanged();

    private void HandleRefreshAuthenticationChanged(object? sender, EventArgs args)
    {
        if (ViewModel is not { } model)
            return;
        _refreshCoordinator?.OnAuthenticationChanged(Authentication.CurrentUserToken, model.CurrentUserId);
        _changeNotificationForwarder?.ReconcileUserScope();
    }
}
