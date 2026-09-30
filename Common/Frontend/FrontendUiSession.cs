using PasswordManagerLocal.Common.Frontend.Services;
using PasswordManagerLocal.Common.Frontend.ViewModels;
using PasswordManagerLocal.Common.Frontend.Views;

namespace PasswordManagerLocal.Common.Frontend;

/// <summary>
/// Owns one frontend graph. Construct, initialize and begin disposal on the UI thread.
/// The Android host transfers its interactive client here; the service retains background ownership.
/// </summary>
public sealed class FrontendUiSession : IAsyncDisposable
{
    private readonly FrontendApplicationContext _context;
    private readonly bool _ownsBackendClient;
    private readonly object _disposalGate = new();
    private Task? _initialization;
    private Task? _disposal;
    private int _disposed;

    public FrontendUiSession(MainView view, FrontendApplicationContext context, bool ownsBackendClient)
    {
        View = view ?? throw new ArgumentNullException(nameof(view));
        _context = context ?? throw new ArgumentNullException(nameof(context));
        _ownsBackendClient = ownsBackendClient;
    }

    public Guid Id { get; } = Guid.NewGuid();
    public MainView View { get; }
    public MainViewModel? ViewModel { get; private set; }
    public AuthSessionRegistry Authentication { get; } = new();
    public FrontendPlatformServices PlatformServices => _context.PlatformServices;
    public bool IsDisposed => Volatile.Read(ref _disposed) != 0;

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
            await ViewModel.InitializeAsync();
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
}
