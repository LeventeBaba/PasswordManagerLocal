using System.Text.Json;
using PasswordManagerLocal.Common.Contracts.Preferences;

namespace PasswordManagerLocal.Common.Frontend.Localization;

public sealed class FrontendLocalizationService : IAsyncDisposable
{
    private readonly object _gate = new();
    private readonly IFrontendLocalizationResourceLoader _resourceLoader;
    private readonly IFrontendLocalizationDiagnostics _diagnostics;
    private FrontendTranslationSnapshot? _activeSnapshot;
    private CancellationTokenSource? _pendingRequest;
    private long _requestGeneration;
    private int _disposed;

    public FrontendLocalizationService(
        IFrontendLocalizationResourceLoader? resourceLoader = null,
        IFrontendLocalizationDiagnostics? diagnostics = null)
    {
        _resourceLoader = resourceLoader ?? new AvaloniaFrontendLocalizationResourceLoader();
        _diagnostics = diagnostics ?? new TraceFrontendLocalizationDiagnostics();
    }

    public AppLanguage? ActiveLanguage => Volatile.Read(ref _activeSnapshot)?.Language;

    public int ActiveTranslationCount => Volatile.Read(ref _activeSnapshot)?.Translations.Count ?? 0;

    public bool HasActiveSnapshot => Volatile.Read(ref _activeSnapshot) is not null;

    public string GetString(string key)
    {
        var snapshot = Volatile.Read(ref _activeSnapshot);
        if (!string.IsNullOrWhiteSpace(key) &&
            snapshot is not null &&
            snapshot.Translations.TryGetValue(key, out var value))
        {
            return value;
        }

        try { _diagnostics.ReportMissingKey(snapshot?.Language); }
        catch
        {
            // Diagnostic observers must never suppress the visible missing-key marker.
        }
        return CreateDiagnosticMarker(key);
    }

    public async Task<FrontendLocalizationChangeResult> SetLanguageAsync(
        AppLanguage language,
        CancellationToken cancellationToken = default)
    {
        var preparation = await PrepareLanguageChangeAsync(language, cancellationToken)
            .ConfigureAwait(false);
        if (preparation.Candidate is not { } candidate)
            return new FrontendLocalizationChangeResult(preparation.Status);

        using (candidate)
        {
            return TryActivate(candidate)
                ? new FrontendLocalizationChangeResult(FrontendLocalizationChangeStatus.Applied)
                : new FrontendLocalizationChangeResult(
                    IsDisposed
                        ? FrontendLocalizationChangeStatus.Disposed
                        : FrontendLocalizationChangeStatus.Superseded);
        }
    }

    internal async Task<FrontendLocalizationPreparation> PrepareLanguageChangeAsync(
        AppLanguage language,
        CancellationToken cancellationToken)
    {
        CancellationTokenSource? previousRequest;
        CancellationTokenSource? request;
        long generation;
        lock (_gate)
        {
            if (IsDisposed)
            {
                return new FrontendLocalizationPreparation(
                    FrontendLocalizationChangeStatus.Disposed,
                    null);
            }

            generation = ++_requestGeneration;
            previousRequest = _pendingRequest;
            _pendingRequest = null;
            if (_activeSnapshot?.Language == language)
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
        {
            return new FrontendLocalizationPreparation(
                FrontendLocalizationChangeStatus.Unchanged,
                null);
        }

        try
        {
            await using var stream = await _resourceLoader
                .OpenAsync(language, request.Token)
                .ConfigureAwait(false);
            var translations = await FrontendTranslationResourceParser
                .ParseAsync(stream, request.Token)
                .ConfigureAwait(false);
            request.Token.ThrowIfCancellationRequested();

            lock (_gate)
            {
                if (IsDisposed)
                {
                    ClearPendingRequest(request);
                    request.Dispose();
                    return new FrontendLocalizationPreparation(
                        FrontendLocalizationChangeStatus.Disposed,
                        null);
                }

                if (generation != _requestGeneration || !ReferenceEquals(_pendingRequest, request))
                {
                    request.Dispose();
                    return new FrontendLocalizationPreparation(
                        FrontendLocalizationChangeStatus.Superseded,
                        null);
                }
            }

            return new FrontendLocalizationPreparation(
                FrontendLocalizationChangeStatus.Applied,
                new FrontendLocalizationCandidate(
                    this,
                    generation,
                    new FrontendTranslationSnapshot(language, translations),
                    request));
        }
        catch (OperationCanceledException)
        {
            var status = GetCancellationStatus(generation, cancellationToken);
            ClearPendingRequest(request);
            request.Dispose();
            return new FrontendLocalizationPreparation(status, null);
        }
        catch (Exception exception) when (exception is IOException or JsonException or InvalidDataException)
        {
            ClearPendingRequest(request);
            request.Dispose();
            ReportLoadFailure(
                language,
                exception is InvalidDataException or JsonException
                    ? FrontendLocalizationFailureKind.InvalidResource
                    : FrontendLocalizationFailureKind.ResourceUnavailable,
                exception.GetType());
            return new FrontendLocalizationPreparation(
                FrontendLocalizationChangeStatus.Failed,
                null);
        }
        catch (Exception exception)
        {
            ClearPendingRequest(request);
            request.Dispose();
            ReportLoadFailure(
                language,
                FrontendLocalizationFailureKind.UnexpectedFailure,
                exception.GetType());
            return new FrontendLocalizationPreparation(
                FrontendLocalizationChangeStatus.Failed,
                null);
        }
    }

    internal bool IsCurrent(FrontendLocalizationCandidate candidate)
    {
        ArgumentNullException.ThrowIfNull(candidate);
        lock (_gate)
        {
            return !IsDisposed &&
                candidate.Generation == _requestGeneration &&
                ReferenceEquals(_pendingRequest, candidate.RequestCancellation);
        }
    }

    internal bool TryActivate(FrontendLocalizationCandidate candidate)
    {
        ArgumentNullException.ThrowIfNull(candidate);
        lock (_gate)
        {
            if (IsDisposed ||
                candidate.Generation != _requestGeneration ||
                !ReferenceEquals(_pendingRequest, candidate.RequestCancellation) ||
                candidate.RequestCancellation.IsCancellationRequested)
            {
                return false;
            }

            _pendingRequest = null;
            Volatile.Write(ref _activeSnapshot, candidate.Snapshot);
        }

        candidate.MarkCommitted();
        return true;
    }

    internal void Abandon(FrontendLocalizationCandidate candidate)
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
            if (Interlocked.Exchange(ref _disposed, 1) != 0)
                return;
            _requestGeneration++;
            pending = _pendingRequest;
            _pendingRequest = null;
            Volatile.Write(ref _activeSnapshot, null);
        }

        CancelSafely(pending);
    }

    public ValueTask DisposeAsync()
    {
        Deactivate();
        return ValueTask.CompletedTask;
    }

    private bool IsDisposed => Volatile.Read(ref _disposed) != 0;

    private FrontendLocalizationChangeStatus GetCancellationStatus(
        long generation,
        CancellationToken callerToken)
    {
        lock (_gate)
        {
            if (IsDisposed)
                return FrontendLocalizationChangeStatus.Disposed;
            if (generation != _requestGeneration)
                return FrontendLocalizationChangeStatus.Superseded;
        }

        return callerToken.IsCancellationRequested
            ? FrontendLocalizationChangeStatus.Canceled
            : FrontendLocalizationChangeStatus.Superseded;
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
            // Cancellation callbacks are external observers; one cannot block retirement.
        }
    }

    private void ReportLoadFailure(
        AppLanguage language,
        FrontendLocalizationFailureKind kind,
        Type exceptionType)
    {
        try { _diagnostics.ReportLoadFailure(language, kind, exceptionType); }
        catch
        {
            // A diagnostic observer cannot turn a failed resource load into a late mutation.
        }
    }

    private static string CreateDiagnosticMarker(string? key)
    {
        if (string.IsNullOrWhiteSpace(key))
            return "[?]";
        var safeKey = new string(key
            .Take(80)
            .Select(character => char.IsLetterOrDigit(character) || character is '_' or '.' or '-'
                ? character
                : '?')
            .ToArray());
        return $"[{safeKey}]";
    }
}

