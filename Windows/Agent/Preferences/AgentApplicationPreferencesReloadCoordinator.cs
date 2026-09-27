using PasswordManagerLocal.Windows.Agent.Localization;
using PasswordManagerLocal.Windows.Agent.Tray;
using System.Diagnostics;

namespace PasswordManagerLocal.Windows.Agent.Preferences;

public sealed class AgentApplicationPreferencesReloadCoordinator : IAgentApplicationPreferencesReloadCoordinator
{
    private readonly WindowsAgentApplicationPreferencesReader _preferencesReader;
    private readonly AgentLocalizer _localizer;
    private readonly ITrayIconController _trayIcon;
    private readonly IApplicationPreferencesFileStampProvider _stampProvider;
    private readonly object _gate = new();
    private readonly CancellationTokenSource _lifetime = new();
    private ApplicationPreferencesFileStamp _loadedStamp;
    private long _requestedGeneration;
    private Task<bool>? _workerTask;
    private Task? _stampCheckTask;
    private bool _shellTextDirty;
    private int _disposed;

    public AgentApplicationPreferencesReloadCoordinator(
        WindowsAgentApplicationPreferencesReader preferencesReader,
        AgentLocalizer localizer,
        ITrayIconController trayIcon,
        IApplicationPreferencesFileStampProvider stampProvider,
        ApplicationPreferencesFileStamp initialStamp)
    {
        _preferencesReader = preferencesReader ?? throw new ArgumentNullException(nameof(preferencesReader));
        _localizer = localizer ?? throw new ArgumentNullException(nameof(localizer));
        _trayIcon = trayIcon ?? throw new ArgumentNullException(nameof(trayIcon));
        _stampProvider = stampProvider ?? throw new ArgumentNullException(nameof(stampProvider));
        _loadedStamp = initialStamp;
        _trayIcon.ContextMenuOpening += HandleContextMenuOpening;
    }

    public Task<bool> ReloadAsync(CancellationToken cancellationToken = default)
    {
        Task<bool> worker;
        lock (_gate)
        {
            if (Volatile.Read(ref _disposed) != 0)
                return Task.FromResult(false);
            _requestedGeneration++;
            worker = _workerTask ??= Task.Run(RunWorkerAsync);
        }
        return cancellationToken.CanBeCanceled ? worker.WaitAsync(cancellationToken) : worker;
    }

    public ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
            return ValueTask.CompletedTask;

        _trayIcon.ContextMenuOpening -= HandleContextMenuOpening;
        _lifetime.Cancel();
        return new ValueTask(CompleteDisposalAsync());
    }

    private async Task CompleteDisposalAsync()
    {
        Task? stampCheck;
        lock (_gate)
            stampCheck = _stampCheckTask;
        if (stampCheck is not null)
            await stampCheck.ConfigureAwait(false);

        Task<bool>? worker;
        lock (_gate)
            worker = _workerTask;
        if (worker is not null)
        {
            try { await worker.ConfigureAwait(false); }
            catch (OperationCanceledException) when (_lifetime.IsCancellationRequested) { }
            catch { }
        }
        _lifetime.Dispose();
    }

    private void HandleContextMenuOpening(object? sender, EventArgs args)
    {
        lock (_gate)
        {
            if (Volatile.Read(ref _disposed) != 0 || _stampCheckTask is { IsCompleted: false })
                return;
            _stampCheckTask = Task.Run(CheckPreferenceStampAsync);
        }
    }

    private async Task CheckPreferenceStampAsync()
    {
        try
        {
            if (Volatile.Read(ref _disposed) != 0)
                return;
            ApplicationPreferencesFileStamp loadedStamp;
            lock (_gate)
                loadedStamp = _loadedStamp;
            if (_stampProvider.GetStamp() != loadedStamp)
                await ObserveReloadAsync().ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            Trace.TraceError($"The Agent preference stamp could not be checked: {exception.GetType().Name}");
        }
    }

    private async Task ObserveReloadAsync()
    {
        try { await ReloadAsync(_lifetime.Token).ConfigureAwait(false); }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested) { }
        catch (Exception exception)
        {
            Trace.TraceError($"The Agent preference recovery reload failed: {exception.GetType().Name}");
        }
    }

    private async Task<bool> RunWorkerAsync()
    {
        var lastResult = false;
        while (true)
        {
            _lifetime.Token.ThrowIfCancellationRequested();
            long generation;
            lock (_gate)
                generation = _requestedGeneration;

            lastResult = await TryReloadGenerationAsync(generation, _lifetime.Token)
                .ConfigureAwait(false);

            lock (_gate)
            {
                if (_requestedGeneration == generation || Volatile.Read(ref _disposed) != 0)
                {
                    _workerTask = null;
                    return lastResult;
                }
            }
        }
    }

    private async Task<bool> TryReloadGenerationAsync(
        long generation,
        CancellationToken cancellationToken)
    {
        try
        {
            var stampBeforeRead = _stampProvider.GetStamp();
            var language = await _preferencesReader.ReadLanguageAsync(cancellationToken)
                .ConfigureAwait(false);
            var candidate = language == _localizer.CurrentLanguage
                ? null
                : await _localizer.LoadCandidateAsync(language, cancellationToken)
                    .ConfigureAwait(false);
            var stampAfterRead = _stampProvider.GetStamp();
            if (stampBeforeRead != stampAfterRead)
            {
                lock (_gate)
                {
                    if (_requestedGeneration == generation && Volatile.Read(ref _disposed) == 0)
                        _requestedGeneration++;
                }
                return false;
            }

            bool updateShellText;
            lock (_gate)
            {
                if (_requestedGeneration != generation || Volatile.Read(ref _disposed) != 0)
                    return false;
                if (candidate is not null)
                {
                    _localizer.Activate(candidate);
                    _shellTextDirty = true;
                }
                updateShellText = _shellTextDirty;
            }

            if (updateShellText)
            {
                await _trayIcon.UpdateTextAsync(
                    WindowsAgentTrayText.Create(_localizer),
                    cancellationToken).ConfigureAwait(false);
            }

            lock (_gate)
            {
                if (_requestedGeneration == generation && Volatile.Read(ref _disposed) == 0)
                {
                    if (updateShellText)
                        _shellTextDirty = false;
                    _loadedStamp = stampAfterRead;
                }
            }
            return true;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            Trace.TraceError($"The Agent application preferences could not be reloaded: {exception.GetType().Name}");
            return false;
        }
    }
}
