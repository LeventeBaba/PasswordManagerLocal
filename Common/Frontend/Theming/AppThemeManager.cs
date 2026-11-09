using Avalonia;

namespace PasswordManagerLocal.Common.Frontend.Theming;

/// <summary>
/// Owns one UI session's theme requests. Candidate loads are cancelable and only a
/// complete current candidate can replace the application's one active dictionary.
/// </summary>
public sealed class AppThemeManager : IAsyncDisposable
{
    private readonly object _gate = new();
    private readonly Guid _ownerId = Guid.NewGuid();
    private readonly AppThemeCatalog _catalog;
    private readonly IAppThemeResourceLoader _resourceLoader;
    private readonly IAppThemeDispatcher _dispatcher;
    private readonly IAppThemeResourceHost _resourceHost;
    private readonly IAppThemeDiagnostics _diagnostics;
    private CancellationTokenSource? _pendingRequest;
    private AppThemeDescriptor? _activeDescriptor;
    private long _requestGeneration;
    private int _deactivated;
    private int _disposed;

    public AppThemeManager(
        AppThemeCatalog catalog,
        IAppThemeResourceLoader resourceLoader,
        IAppThemeDispatcher dispatcher,
        IAppThemeResourceHost resourceHost,
        IAppThemeDiagnostics? diagnostics = null)
    {
        _catalog = catalog ?? throw new ArgumentNullException(nameof(catalog));
        _resourceLoader = resourceLoader ?? throw new ArgumentNullException(nameof(resourceLoader));
        _dispatcher = dispatcher ?? throw new ArgumentNullException(nameof(dispatcher));
        _resourceHost = resourceHost ?? throw new ArgumentNullException(nameof(resourceHost));
        _diagnostics = diagnostics ?? new TraceAppThemeDiagnostics();
        _resourceHost.Register(_ownerId);
    }

    public AppThemeCatalog Catalog => _catalog;

    public AppThemeId? ActiveThemeId => Volatile.Read(ref _activeDescriptor)?.Id;

    public ThemeBrightness? ActiveBrightness => Volatile.Read(ref _activeDescriptor)?.Brightness;

    public static AppThemeManager CreateDefault()
    {
        IAppThemeResourceHost host = Application.Current is App app
            ? app.ThemeResourceHost
            : new DetachedAppThemeResourceHost();
        return new AppThemeManager(
            AppThemeCatalog.CreateDefault(),
            new AvaloniaAppThemeResourceLoader(),
            new AvaloniaAppThemeDispatcher(),
            host);
    }

    public async Task<AppThemeChangeResult> SetThemeAsync(
        AppThemeId theme,
        CancellationToken cancellationToken = default)
    {
        var preparation = await PrepareThemeChangeAsync(theme, cancellationToken)
            .ConfigureAwait(false);
        if (preparation.Candidate is not { } candidate)
            return new AppThemeChangeResult(preparation.Status);

        using (candidate)
        {
            var status = await TryActivateAsync(candidate, cancellationToken)
                .ConfigureAwait(false);
            return new AppThemeChangeResult(status);
        }
    }

    internal async Task<AppThemePreparation> PrepareThemeChangeAsync(
        AppThemeId theme,
        CancellationToken cancellationToken)
    {
        if (!_catalog.TryGet(theme, out var descriptor))
        {
            ReportFailure(theme, AppThemeFailureKind.InvalidResource, typeof(ArgumentOutOfRangeException));
            return new AppThemePreparation(AppThemeChangeStatus.Failed, null);
        }

        CancellationTokenSource? previousRequest;
        CancellationTokenSource? request;
        long generation;
        lock (_gate)
        {
            if (IsDeactivated)
                return new AppThemePreparation(AppThemeChangeStatus.Disposed, null);

            generation = ++_requestGeneration;
            previousRequest = _pendingRequest;
            _pendingRequest = null;
            if (_activeDescriptor?.Id == theme && _resourceHost.ActiveThemeId == theme)
            {
                request = null;
            }
            else
            {
                request = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                _pendingRequest = request;
            }
        }

        CancelSafely(previousRequest);
        if (request is null)
            return new AppThemePreparation(AppThemeChangeStatus.Unchanged, null);

        IAppThemeResourceSet? resources = null;
        try
        {
            resources = await _resourceLoader.LoadAsync(descriptor, request.Token)
                .ConfigureAwait(false);
            request.Token.ThrowIfCancellationRequested();
            if (resources.ThemeId != theme || ThemeContract.FindViolations(resources).Count != 0)
                throw new InvalidDataException("The selected theme does not satisfy the semantic resource contract.");

            lock (_gate)
            {
                if (IsDeactivated)
                {
                    ClearPendingRequest(request);
                    resources.Dispose();
                    request.Dispose();
                    return new AppThemePreparation(AppThemeChangeStatus.Disposed, null);
                }
                if (generation != _requestGeneration || !ReferenceEquals(_pendingRequest, request))
                {
                    resources.Dispose();
                    request.Dispose();
                    return new AppThemePreparation(AppThemeChangeStatus.Superseded, null);
                }
            }

            return new AppThemePreparation(
                AppThemeChangeStatus.Applied,
                new AppThemeCandidate(this, generation, descriptor, resources, request));
        }
        catch (OperationCanceledException)
        {
            resources?.Dispose();
            var status = GetCancellationStatus(generation, cancellationToken);
            ClearPendingRequest(request);
            request.Dispose();
            return new AppThemePreparation(status, null);
        }
        catch (Exception exception)
        {
            resources?.Dispose();
            ClearPendingRequest(request);
            request.Dispose();
            ReportFailure(
                theme,
                exception is InvalidDataException
                    ? AppThemeFailureKind.InvalidResource
                    : exception is IOException
                        ? AppThemeFailureKind.ResourceUnavailable
                        : AppThemeFailureKind.UnexpectedFailure,
                exception.GetType());
            return new AppThemePreparation(AppThemeChangeStatus.Failed, null);
        }
    }

    internal bool IsCurrent(AppThemeCandidate candidate)
    {
        ArgumentNullException.ThrowIfNull(candidate);
        lock (_gate)
        {
            return !IsDeactivated &&
                candidate.Generation == _requestGeneration &&
                ReferenceEquals(_pendingRequest, candidate.RequestCancellation);
        }
    }

    internal async Task<AppThemeChangeStatus> TryActivateAsync(
        AppThemeCandidate candidate,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(candidate);
        using var linkedCancellation = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken,
            candidate.CancellationToken);
        var status = AppThemeChangeStatus.Superseded;
        try
        {
            await _dispatcher.InvokeAsync(() =>
            {
                lock (_gate)
                {
                    if (IsDeactivated)
                    {
                        status = AppThemeChangeStatus.Disposed;
                        return;
                    }
                    if (candidate.Generation != _requestGeneration ||
                        !ReferenceEquals(_pendingRequest, candidate.RequestCancellation) ||
                        candidate.RequestCancellation.IsCancellationRequested)
                    {
                        status = AppThemeChangeStatus.Superseded;
                        return;
                    }

                    _resourceHost.Replace(_ownerId, candidate.Descriptor, candidate.Resources);
                    _pendingRequest = null;
                    Volatile.Write(ref _activeDescriptor, candidate.Descriptor);
                    candidate.MarkCommitted();
                    status = AppThemeChangeStatus.Applied;
                }
            }, linkedCancellation.Token).ConfigureAwait(false);
            return status;
        }
        catch (OperationCanceledException)
        {
            return GetCancellationStatus(candidate.Generation, cancellationToken);
        }
        catch (Exception exception)
        {
            ReportFailure(
                candidate.Descriptor.Id,
                AppThemeFailureKind.ApplyFailed,
                exception.GetType());
            return AppThemeChangeStatus.Failed;
        }
    }

    internal void Abandon(AppThemeCandidate candidate)
    {
        lock (_gate)
        {
            if (ReferenceEquals(_pendingRequest, candidate.RequestCancellation))
                _pendingRequest = null;
        }
    }

    public void Deactivate()
    {
        CancellationTokenSource? pending;
        lock (_gate)
        {
            if (Interlocked.Exchange(ref _deactivated, 1) != 0)
                return;
            _requestGeneration++;
            pending = _pendingRequest;
            _pendingRequest = null;
        }
        CancelSafely(pending);
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
            return;
        Deactivate();
        try
        {
            await _dispatcher.InvokeAsync(
                () => _resourceHost.Release(_ownerId),
                CancellationToken.None).ConfigureAwait(false);
        }
        finally
        {
            Volatile.Write(ref _activeDescriptor, null);
        }
    }

    private AppThemeChangeStatus GetCancellationStatus(
        long generation,
        CancellationToken callerToken)
    {
        lock (_gate)
        {
            if (IsDeactivated)
                return AppThemeChangeStatus.Disposed;
            if (generation != _requestGeneration)
                return AppThemeChangeStatus.Superseded;
        }
        return callerToken.IsCancellationRequested
            ? AppThemeChangeStatus.Canceled
            : AppThemeChangeStatus.Superseded;
    }

    private void ClearPendingRequest(CancellationTokenSource request)
    {
        lock (_gate)
        {
            if (ReferenceEquals(_pendingRequest, request))
                _pendingRequest = null;
        }
    }

    private static void CancelSafely(CancellationTokenSource? request)
    {
        if (request is null)
            return;
        try { request.Cancel(); }
        catch
        {
            // External cancellation callbacks cannot block candidate retirement.
        }
    }

    private bool IsDeactivated => Volatile.Read(ref _deactivated) != 0;

    private void ReportFailure(
        AppThemeId theme,
        AppThemeFailureKind failureKind,
        Type exceptionType)
    {
        try { _diagnostics.ReportFailure(theme, failureKind, exceptionType); }
        catch
        {
            // Diagnostics cannot turn a rejected theme into a late mutation.
        }
    }
}
