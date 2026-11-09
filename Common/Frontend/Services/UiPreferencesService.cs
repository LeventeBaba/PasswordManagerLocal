using PasswordManagerLocal.Common.Contracts.Preferences;
using PasswordManagerLocal.Common.Frontend.Localization;
using PasswordManagerLocal.Common.Frontend.Theming;
using PasswordManagerLocal.Common.Preferences;

namespace PasswordManagerLocal.Common.Frontend.Services;

/// <summary>
/// Owns one UI session's localization snapshot and serializes application-preference writes.
/// Callers may await Set*Async directly or use Request* from two-way UI bindings; requested
/// operations are tracked, observed and canceled when the UI session is deactivated.
/// </summary>
public sealed class UiPreferencesService : IAsyncDisposable
{
    private readonly IApplicationPreferencesStore _store;
    private readonly IApplicationPreferencesChangeNotifier? _changeNotifier;
    private readonly IUiPreferencesDiagnostics _diagnostics;
    private readonly FrontendLocalizationService _localization;
    private readonly AppThemeManager _themeManager;
    private readonly bool _ownsPlatformServices;
    private readonly SemaphoreSlim _writeGate = new(1, 1);
    private readonly CancellationTokenSource _lifetime = new();
    private readonly object _operationGate = new();
    private readonly HashSet<Task> _operations = [];
    private ApplicationPreferences _current;
    private int _deactivated;
    private int _disposed;

    private UiPreferencesService(
        IApplicationPreferencesStore store,
        IApplicationPreferencesChangeNotifier? changeNotifier,
        FrontendPlatformServices platformServices,
        bool ownsPlatformServices,
        ApplicationPreferences initialPreferences,
        FrontendLocalizationService localization,
        AppThemeManager themeManager,
        IUiPreferencesDiagnostics diagnostics)
    {
        _store = store;
        _changeNotifier = changeNotifier;
        PlatformServices = platformServices;
        _ownsPlatformServices = ownsPlatformServices;
        _current = initialPreferences;
        _localization = localization;
        _themeManager = themeManager;
        _diagnostics = diagnostics;
        MotionPolicy = new InterfaceMotionPolicy(PlatformServices, _current.InterfaceAnimationsEnabled);
    }

    public static async Task<UiPreferencesService> CreateAsync(
        IApplicationPreferencesStore store,
        IApplicationPreferencesChangeNotifier? changeNotifier = null,
        FrontendPlatformServices? platformServices = null,
        ApplicationPreferences? initialPreferences = null,
        IFrontendLocalizationResourceLoader? localizationResourceLoader = null,
        IFrontendLocalizationDiagnostics? localizationDiagnostics = null,
        AppThemeManager? themeManager = null,
        IUiPreferencesDiagnostics? diagnostics = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(store);
        var preferences = initialPreferences ?? await store.ReadAsync(cancellationToken);
        var localization = new FrontendLocalizationService(
            localizationResourceLoader,
            localizationDiagnostics);
        var loadResult = await localization.SetLanguageAsync(
            preferences.Language,
            cancellationToken);
        if (!loadResult.IsSuccess || !localization.HasActiveSnapshot)
        {
            await localization.DisposeAsync();
            cancellationToken.ThrowIfCancellationRequested();
            throw new InvalidDataException(
                "The selected frontend localization resource could not be initialized.");
        }

        var resolvedThemeManager = themeManager ?? AppThemeManager.CreateDefault();
        var themeResult = await resolvedThemeManager.SetThemeAsync(
            preferences.Theme,
            cancellationToken);
        if (!themeResult.IsSuccess || resolvedThemeManager.ActiveThemeId != preferences.Theme)
        {
            await localization.DisposeAsync();
            await resolvedThemeManager.DisposeAsync();
            cancellationToken.ThrowIfCancellationRequested();
            throw new InvalidDataException(
                "The selected frontend theme resource could not be initialized.");
        }

        var resolvedPlatformServices = platformServices ?? new FrontendPlatformServices();
        return new UiPreferencesService(
            store,
            changeNotifier,
            resolvedPlatformServices,
            platformServices is null,
            preferences,
            localization,
            resolvedThemeManager,
            diagnostics ?? new TraceUiPreferencesDiagnostics());
    }

    public FrontendPlatformServices PlatformServices { get; }

    public InterfaceMotionPolicy MotionPolicy { get; }

    public event EventHandler<UiPreferencesChangedEventArgs>? PreferencesChanged;

    public event EventHandler<UiPreferenceUpdateCompletedEventArgs>? PreferenceUpdateCompleted;

    public AppLanguage CurrentLanguage => Volatile.Read(ref _current).Language;

    public AppThemeId CurrentThemeId => Volatile.Read(ref _current).Theme;

    public ThemeBrightness CurrentThemeBrightness =>
        _themeManager.ActiveBrightness ??
        _themeManager.Catalog.GetRequired(CurrentThemeId).Brightness;

    public IReadOnlyList<AppThemeDescriptor> AvailableThemes => _themeManager.Catalog.Descriptors;

    public bool InterfaceAnimationsEnabled =>
        Volatile.Read(ref _current).InterfaceAnimationsEnabled;

    internal FrontendLocalizationService Localization => _localization;

    public string GetString(string key) => _localization.GetString(key);

    public Task<UiPreferenceUpdateResult> SetLanguageAsync(
        AppLanguage language,
        CancellationToken cancellationToken = default) =>
        StartTracked(
            UiPreferenceKind.Language,
            () => SetLanguageCoreAsync(language, cancellationToken));

    public Task<UiPreferenceUpdateResult> SetThemeAsync(
        AppThemeId theme,
        CancellationToken cancellationToken = default) =>
        StartTracked(
            UiPreferenceKind.Theme,
            () => SetThemeCoreAsync(theme, cancellationToken));

    public Task<UiPreferenceUpdateResult> SetInterfaceAnimationsEnabledAsync(
        bool enabled,
        CancellationToken cancellationToken = default) =>
        StartTracked(
            UiPreferenceKind.InterfaceAnimations,
            () => SetInterfaceAnimationsEnabledCoreAsync(enabled, cancellationToken));

    public void RequestLanguageChange(AppLanguage language) =>
        StartTracked(
            UiPreferenceKind.Language,
            () => SetLanguageCoreAsync(language, CancellationToken.None));

    public void RequestThemeChange(AppThemeId theme) =>
        StartTracked(
            UiPreferenceKind.Theme,
            () => SetThemeCoreAsync(theme, CancellationToken.None));

    public void RequestInterfaceAnimationsEnabledChange(bool enabled) =>
        StartTracked(
            UiPreferenceKind.InterfaceAnimations,
            () => SetInterfaceAnimationsEnabledCoreAsync(enabled, CancellationToken.None));

    public void Deactivate()
    {
        lock (_operationGate)
        {
            if (Interlocked.Exchange(ref _deactivated, 1) != 0)
                return;
            PreferencesChanged = null;
            PreferenceUpdateCompleted = null;
        }
        try { _lifetime.Cancel(); }
        catch
        {
            // A cancellation callback cannot prevent synchronous session retirement.
        }
        _localization.Deactivate();
        _themeManager.Deactivate();
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
            return;
        Deactivate();

        Task[] operations;
        lock (_operationGate)
            operations = [.. _operations];
        if (operations.Length != 0)
        {
            try { await Task.WhenAll(operations).ConfigureAwait(false); }
            catch
            {
                // Track observes every operation. This protects disposal from a custom
                // caller or observer throwing outside the normal result-based contract.
            }
        }

        await _localization.DisposeAsync();
        await _themeManager.DisposeAsync();
        if (_ownsPlatformServices)
            PlatformServices.Dispose();
        _writeGate.Dispose();
        _lifetime.Dispose();
    }

    private async Task<UiPreferenceUpdateResult> SetLanguageCoreAsync(
        AppLanguage language,
        CancellationToken callerToken)
    {
        if (IsDeactivated)
            return Complete(new UiPreferenceUpdateResult(
                UiPreferenceKind.Language,
                UiPreferenceUpdateStatus.Disposed));

        FrontendLocalizationPreparation preparation;
        try
        {
            preparation = await _localization.PrepareLanguageChangeAsync(language, callerToken);
        }
        catch (Exception exception)
        {
            ReportFailure(
                UiPreferenceKind.Language,
                UiPreferenceFailureStage.LoadLocalization,
                exception.GetType());
            return Complete(new UiPreferenceUpdateResult(
                UiPreferenceKind.Language,
                UiPreferenceUpdateStatus.Failed));
        }

        if (preparation.Candidate is not { } candidate)
        {
            return Complete(new UiPreferenceUpdateResult(
                UiPreferenceKind.Language,
                Map(preparation.Status)));
        }

        using (candidate)
        using (var operationCancellation = CancellationTokenSource.CreateLinkedTokenSource(
                   callerToken,
                   _lifetime.Token,
                   candidate.CancellationToken))
        {
            var token = operationCancellation.Token;
            var enteredGate = false;
            try
            {
                await _writeGate.WaitAsync(token);
                enteredGate = true;
                if (!_localization.IsCurrent(candidate))
                {
                    return Complete(new UiPreferenceUpdateResult(
                        UiPreferenceKind.Language,
                        IsDeactivated
                            ? UiPreferenceUpdateStatus.Disposed
                            : UiPreferenceUpdateStatus.Superseded));
                }

                var previous = Volatile.Read(ref _current);
                var updated = previous with { Language = language };
                try
                {
                    await _store.WriteAsync(updated, token);
                }
                catch (OperationCanceledException)
                {
                    var rollbackFailed = !await TryRollbackAsync(
                        UiPreferenceKind.Language,
                        previous);
                    return Complete(new UiPreferenceUpdateResult(
                        UiPreferenceKind.Language,
                        GetCancellationStatus(callerToken, candidate),
                        PersistenceStateUncertain: rollbackFailed));
                }
                catch (Exception exception)
                {
                    ReportFailure(
                        UiPreferenceKind.Language,
                        UiPreferenceFailureStage.Persist,
                        exception.GetType());
                    var rollbackFailed = !await TryRollbackAsync(
                        UiPreferenceKind.Language,
                        previous);
                    return Complete(new UiPreferenceUpdateResult(
                        UiPreferenceKind.Language,
                        UiPreferenceUpdateStatus.Failed,
                        PersistenceStateUncertain: rollbackFailed));
                }

                var committed = false;
                lock (_operationGate)
                {
                    if (!IsDeactivated && _localization.TryActivate(candidate))
                    {
                        Volatile.Write(ref _current, updated);
                        RaisePreferencesChanged(
                            new UiPreferencesChangedEventArgs(
                                true,
                                false,
                                false,
                                language,
                                updated.Theme));
                        committed = true;
                    }
                }

                if (!committed)
                {
                    var rollbackFailed = !await TryRollbackAsync(
                        UiPreferenceKind.Language,
                        previous);
                    return Complete(new UiPreferenceUpdateResult(
                        UiPreferenceKind.Language,
                        IsDeactivated
                            ? UiPreferenceUpdateStatus.Disposed
                            : UiPreferenceUpdateStatus.Superseded,
                        PersistenceStateUncertain: rollbackFailed));
                }

                var notificationFailed = false;
                if (_changeNotifier is not null && !IsDeactivated)
                {
                    try
                    {
                        await _changeNotifier.NotifyLanguagePersistedAsync(_lifetime.Token);
                    }
                    catch (OperationCanceledException) when (_lifetime.IsCancellationRequested)
                    {
                    }
                    catch (Exception exception)
                    {
                        notificationFailed = true;
                        ReportFailure(
                            UiPreferenceKind.Language,
                            UiPreferenceFailureStage.NotifyAgent,
                            exception.GetType());
                    }
                }

                return Complete(new UiPreferenceUpdateResult(
                    UiPreferenceKind.Language,
                    UiPreferenceUpdateStatus.Applied,
                    AgentNotificationFailed: notificationFailed));
            }
            catch (OperationCanceledException)
            {
                return Complete(new UiPreferenceUpdateResult(
                    UiPreferenceKind.Language,
                    GetCancellationStatus(callerToken, candidate)));
            }
            finally
            {
                if (enteredGate)
                    _writeGate.Release();
            }
        }
    }

    private async Task<UiPreferenceUpdateResult> SetThemeCoreAsync(
        AppThemeId theme,
        CancellationToken callerToken)
    {
        if (IsDeactivated)
        {
            return Complete(new UiPreferenceUpdateResult(
                UiPreferenceKind.Theme,
                UiPreferenceUpdateStatus.Disposed));
        }

        var preparation = await _themeManager.PrepareThemeChangeAsync(theme, callerToken);
        if (preparation.Candidate is not { } candidate)
        {
            return Complete(new UiPreferenceUpdateResult(
                UiPreferenceKind.Theme,
                Map(preparation.Status)));
        }

        using (candidate)
        using (var operationCancellation = CancellationTokenSource.CreateLinkedTokenSource(
                   callerToken,
                   _lifetime.Token,
                   candidate.CancellationToken))
        {
            var token = operationCancellation.Token;
            var enteredGate = false;
            try
            {
                await _writeGate.WaitAsync(token);
                enteredGate = true;
                if (!_themeManager.IsCurrent(candidate))
                {
                    return Complete(new UiPreferenceUpdateResult(
                        UiPreferenceKind.Theme,
                        IsDeactivated
                            ? UiPreferenceUpdateStatus.Disposed
                            : UiPreferenceUpdateStatus.Superseded));
                }

                var previous = Volatile.Read(ref _current);
                var updated = previous with { Theme = theme };
                try
                {
                    await _store.WriteAsync(updated, token);
                }
                catch (OperationCanceledException)
                {
                    var rollbackFailed = !await TryRollbackAsync(UiPreferenceKind.Theme, previous);
                    return Complete(new UiPreferenceUpdateResult(
                        UiPreferenceKind.Theme,
                        GetThemeCancellationStatus(callerToken, candidate),
                        PersistenceStateUncertain: rollbackFailed));
                }
                catch (Exception exception)
                {
                    ReportFailure(UiPreferenceKind.Theme, UiPreferenceFailureStage.Persist, exception.GetType());
                    var rollbackFailed = !await TryRollbackAsync(UiPreferenceKind.Theme, previous);
                    return Complete(new UiPreferenceUpdateResult(
                        UiPreferenceKind.Theme,
                        UiPreferenceUpdateStatus.Failed,
                        PersistenceStateUncertain: rollbackFailed));
                }

                var activationStatus = await _themeManager.TryActivateAsync(candidate, token);
                if (activationStatus != AppThemeChangeStatus.Applied)
                {
                    var rollbackFailed = !await TryRollbackAsync(UiPreferenceKind.Theme, previous);
                    return Complete(new UiPreferenceUpdateResult(
                        UiPreferenceKind.Theme,
                        Map(activationStatus),
                        PersistenceStateUncertain: rollbackFailed));
                }

                Volatile.Write(ref _current, updated);
                RaisePreferencesChanged(new UiPreferencesChangedEventArgs(
                    false,
                    true,
                    false,
                    updated.Language,
                    updated.Theme));
                return Complete(new UiPreferenceUpdateResult(
                    UiPreferenceKind.Theme,
                    UiPreferenceUpdateStatus.Applied));
            }
            catch (OperationCanceledException)
            {
                return Complete(new UiPreferenceUpdateResult(
                    UiPreferenceKind.Theme,
                    GetThemeCancellationStatus(callerToken, candidate)));
            }
            finally
            {
                if (enteredGate)
                    _writeGate.Release();
            }
        }
    }

    private Task<UiPreferenceUpdateResult> SetInterfaceAnimationsEnabledCoreAsync(
        bool enabled,
        CancellationToken callerToken) =>
        SetSimplePreferenceAsync(
            UiPreferenceKind.InterfaceAnimations,
            current => current.InterfaceAnimationsEnabled == enabled,
            current => current with { InterfaceAnimationsEnabled = enabled },
            updated =>
            {
                MotionPolicy.SetUserPreference(updated.InterfaceAnimationsEnabled);
                RaisePreferencesChanged(new UiPreferencesChangedEventArgs(
                    false,
                    false,
                    true,
                    updated.Language,
                    updated.Theme));
            },
            callerToken);

    private async Task<UiPreferenceUpdateResult> SetSimplePreferenceAsync(
        UiPreferenceKind kind,
        Func<ApplicationPreferences, bool> isUnchanged,
        Func<ApplicationPreferences, ApplicationPreferences> update,
        Action<ApplicationPreferences> apply,
        CancellationToken callerToken)
    {
        if (IsDeactivated)
        {
            return Complete(new UiPreferenceUpdateResult(
                kind,
                UiPreferenceUpdateStatus.Disposed));
        }

        using var operationCancellation = CancellationTokenSource.CreateLinkedTokenSource(
            callerToken,
            _lifetime.Token);
        var enteredGate = false;
        try
        {
            await _writeGate.WaitAsync(operationCancellation.Token);
            enteredGate = true;
            if (IsDeactivated)
            {
                return Complete(new UiPreferenceUpdateResult(
                    kind,
                    UiPreferenceUpdateStatus.Disposed));
            }

            var current = Volatile.Read(ref _current);
            if (isUnchanged(current))
            {
                return Complete(new UiPreferenceUpdateResult(
                    kind,
                    UiPreferenceUpdateStatus.Unchanged));
            }

            var updated = update(current);
            try
            {
                await _store.WriteAsync(updated, operationCancellation.Token);
            }
            catch (OperationCanceledException)
            {
                var rollbackFailed = !await TryRollbackAsync(kind, current);
                return Complete(new UiPreferenceUpdateResult(
                    kind,
                    GetCancellationStatus(callerToken),
                    PersistenceStateUncertain: rollbackFailed));
            }
            catch (Exception exception)
            {
                ReportFailure(kind, UiPreferenceFailureStage.Persist, exception.GetType());
                var rollbackFailed = !await TryRollbackAsync(kind, current);
                return Complete(new UiPreferenceUpdateResult(
                    kind,
                    UiPreferenceUpdateStatus.Failed,
                    PersistenceStateUncertain: rollbackFailed));
            }

            if (operationCancellation.IsCancellationRequested)
            {
                var rollbackFailed = !await TryRollbackAsync(kind, current);
                return Complete(new UiPreferenceUpdateResult(
                    kind,
                    GetCancellationStatus(callerToken),
                    PersistenceStateUncertain: rollbackFailed));
            }

            var committed = false;
            lock (_operationGate)
            {
                if (!IsDeactivated)
                {
                    Volatile.Write(ref _current, updated);
                    apply(updated);
                    committed = true;
                }
            }

            if (!committed)
            {
                var rollbackFailed = !await TryRollbackAsync(kind, current);
                return Complete(new UiPreferenceUpdateResult(
                    kind,
                    UiPreferenceUpdateStatus.Disposed,
                    PersistenceStateUncertain: rollbackFailed));
            }
            return Complete(new UiPreferenceUpdateResult(
                kind,
                UiPreferenceUpdateStatus.Applied));
        }
        catch (OperationCanceledException)
        {
            return Complete(new UiPreferenceUpdateResult(
                kind,
                GetCancellationStatus(callerToken)));
        }
        finally
        {
            if (enteredGate)
                _writeGate.Release();
        }
    }

    private async Task<bool> TryRollbackAsync(
        UiPreferenceKind kind,
        ApplicationPreferences previous)
    {
        try
        {
            // A store can complete an in-flight write after cancellation. Since the caller
            // already owns the serialized write gate, restore the last committed value even
            // during session teardown; disposal drains this operation before releasing state.
            await _store.WriteAsync(previous, CancellationToken.None);
            return true;
        }
        catch (Exception exception)
        {
            ReportFailure(
                kind,
                UiPreferenceFailureStage.RollbackPersistence,
                exception.GetType());
            return false;
        }
    }

    private Task<UiPreferenceUpdateResult> StartTracked(
        UiPreferenceKind kind,
        Func<Task<UiPreferenceUpdateResult>> start)
    {
        var starter = new TaskCompletionSource();
        Task<UiPreferenceUpdateResult> operation;
        lock (_operationGate)
        {
            if (IsDeactivated)
            {
                return Task.FromResult(new UiPreferenceUpdateResult(
                    kind,
                    UiPreferenceUpdateStatus.Disposed));
            }

            operation = ExecuteTrackedAsync(kind, start, starter.Task);
            _operations.Add(operation);
            operation.GetAwaiter().OnCompleted(() => FinishTrackedOperation(operation));
        }

        starter.TrySetResult();
        return operation;
    }

    private async Task<UiPreferenceUpdateResult> ExecuteTrackedAsync(
        UiPreferenceKind kind,
        Func<Task<UiPreferenceUpdateResult>> start,
        Task starter)
    {
        await starter;
        try
        {
            return await start();
        }
        catch (Exception exception)
        {
            ReportFailure(
                kind,
                UiPreferenceFailureStage.Observer,
                exception.GetType());
            return Complete(new UiPreferenceUpdateResult(
                kind,
                IsDeactivated
                    ? UiPreferenceUpdateStatus.Disposed
                    : UiPreferenceUpdateStatus.Failed));
        }
    }

    private void FinishTrackedOperation(Task operation)
    {
        // Accessing Exception observes any unexpected fault from a custom diagnostic
        // or observer. Normal preference failures are represented by result values.
        _ = operation.Exception;
        lock (_operationGate)
            _operations.Remove(operation);
    }

    private UiPreferenceUpdateResult Complete(UiPreferenceUpdateResult result)
    {
        if (!IsDeactivated)
        {
            var handlers = PreferenceUpdateCompleted;
            if (handlers is not null)
            {
                var args = new UiPreferenceUpdateCompletedEventArgs(result);
                foreach (EventHandler<UiPreferenceUpdateCompletedEventArgs> handler in handlers.GetInvocationList())
                {
                    if (IsDeactivated)
                        break;
                    try { handler(this, args); }
                    catch (Exception exception)
                    {
                        ReportFailure(
                            result.Kind,
                            UiPreferenceFailureStage.Observer,
                            exception.GetType());
                    }
                }
            }
        }

        return result;
    }

    private void RaisePreferencesChanged(UiPreferencesChangedEventArgs args)
    {
        if (IsDeactivated)
            return;
        var handlers = PreferencesChanged;
        if (handlers is null)
            return;
        foreach (EventHandler<UiPreferencesChangedEventArgs> handler in handlers.GetInvocationList())
        {
            if (IsDeactivated)
                break;
            try { handler(this, args); }
            catch (Exception exception)
            {
                ReportFailure(
                    args.LanguageChanged
                        ? UiPreferenceKind.Language
                        : args.ThemeChanged
                            ? UiPreferenceKind.Theme
                            : UiPreferenceKind.InterfaceAnimations,
                    UiPreferenceFailureStage.Observer,
                    exception.GetType());
            }
        }
    }

    private UiPreferenceUpdateStatus GetCancellationStatus(
        CancellationToken callerToken,
        FrontendLocalizationCandidate? candidate = null)
    {
        if (IsDeactivated)
            return UiPreferenceUpdateStatus.Disposed;
        if (candidate is not null && !_localization.IsCurrent(candidate))
            return UiPreferenceUpdateStatus.Superseded;
        return callerToken.IsCancellationRequested
            ? UiPreferenceUpdateStatus.Canceled
            : UiPreferenceUpdateStatus.Superseded;
    }

    private static UiPreferenceUpdateStatus Map(FrontendLocalizationChangeStatus status) =>
        status switch
        {
            FrontendLocalizationChangeStatus.Applied => UiPreferenceUpdateStatus.Applied,
            FrontendLocalizationChangeStatus.Unchanged => UiPreferenceUpdateStatus.Unchanged,
            FrontendLocalizationChangeStatus.Superseded => UiPreferenceUpdateStatus.Superseded,
            FrontendLocalizationChangeStatus.Canceled => UiPreferenceUpdateStatus.Canceled,
            FrontendLocalizationChangeStatus.Disposed => UiPreferenceUpdateStatus.Disposed,
            _ => UiPreferenceUpdateStatus.Failed
        };

    private static UiPreferenceUpdateStatus Map(AppThemeChangeStatus status) =>
        status switch
        {
            AppThemeChangeStatus.Applied => UiPreferenceUpdateStatus.Applied,
            AppThemeChangeStatus.Unchanged => UiPreferenceUpdateStatus.Unchanged,
            AppThemeChangeStatus.Superseded => UiPreferenceUpdateStatus.Superseded,
            AppThemeChangeStatus.Canceled => UiPreferenceUpdateStatus.Canceled,
            AppThemeChangeStatus.Disposed => UiPreferenceUpdateStatus.Disposed,
            _ => UiPreferenceUpdateStatus.Failed
        };

    private UiPreferenceUpdateStatus GetThemeCancellationStatus(
        CancellationToken callerToken,
        AppThemeCandidate candidate)
    {
        if (IsDeactivated)
            return UiPreferenceUpdateStatus.Disposed;
        if (!_themeManager.IsCurrent(candidate))
            return UiPreferenceUpdateStatus.Superseded;
        return callerToken.IsCancellationRequested
            ? UiPreferenceUpdateStatus.Canceled
            : UiPreferenceUpdateStatus.Superseded;
    }

    private bool IsDeactivated => Volatile.Read(ref _deactivated) != 0;

    private void ReportFailure(
        UiPreferenceKind kind,
        UiPreferenceFailureStage stage,
        Type exceptionType)
    {
        try { _diagnostics.ReportFailure(kind, stage, exceptionType); }
        catch
        {
            // Diagnostics are deliberately isolated from preference results and teardown.
        }
    }

}
