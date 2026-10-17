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
    private int _disposed;

    public FrontendUiSession(MainView view, FrontendApplicationContext context, bool ownsBackendClient)
    {
        View = view ?? throw new ArgumentNullException(nameof(view));
        _context = context ?? throw new ArgumentNullException(nameof(context));
        _ownsBackendClient = ownsBackendClient;
        if (_context.BackendClient is IFrontendChangeNotificationClient notificationClient)
        {
            _changeNotificationForwarder = new FrontendChangeNotificationForwarder(
                _context.BackendClient,
                notificationClient,
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
            ViewModel = new MainViewModel(
                new DeferredEndpoints(_context.BackendClient, token),
                _context.BackendClient,
                _context.BackgroundSyncSettingsClient,
                _context.ApplicationPreferencesStore,
                _context.ApplicationPreferencesChangeNotifier,
                Authentication,
                PlatformServices,
                preferences);
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
