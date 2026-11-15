using PasswordManagerLocal.Common.Backend.Abstractions.Services;
using PasswordManagerLocal.Common.Backend.Diagnostics;
using PasswordManagerLocal.Common.Backend.Models;
using PasswordManagerLocal.Common.Backend.Utils;

namespace PasswordManagerLocal.Common.Backend.Services.Hosted;

/// <summary>One backend-owned, coalesced reconciliation loop for committed settings, delivery and health.</summary>
public sealed class RevocationDeliveryRuntimeHostedService(IRevocationDeliveryActivity activity,
    ISyncRuntimeService runtime, TimeProvider? clock = null,
    IBackendExecutionProfileProvider? executionProfile = null) : IBackendHostedService
{
    private readonly TimeProvider _clock = clock ?? TimeProvider.System;
    private CancellationTokenSource? _lifetime;
    private Task? _work;
    private int _insideReconciliation;
    internal static TimeSpan HealthCheckInterval => TimeSpan.FromSeconds(30);
    internal static TimeSpan RetryDelay(int attempt) =>
        TimeSpan.FromSeconds(Math.Min(60, 1 << Math.Clamp(attempt - 1, 0, 6)));

    public Task StartAsync(CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        if (_lifetime is not null) return Task.CompletedTask;
        _lifetime = new();
        var token = _lifetime.Token;
        runtime.StateChanged += OnRuntimeChanged;
        if (executionProfile is not null) executionProfile.ProfileChanged += OnProfileChanged;
        _work = IndependentBackgroundWork.Run(() => RunAsync(token), CancellationToken.None);
        activity.RequestRefresh();
        return Task.CompletedTask;
    }

    public async Task StopAsync(CancellationToken ct = default)
    {
        var lifetime = _lifetime;
        if (lifetime is null) return;
        runtime.StateChanged -= OnRuntimeChanged;
        if (executionProfile is not null) executionProfile.ProfileChanged -= OnProfileChanged;
        lifetime.Cancel();
        try { if (_work is not null) await _work.WaitAsync(ct); }
        catch (OperationCanceledException) when (lifetime.IsCancellationRequested && !ct.IsCancellationRequested) { }
        // Retain ownership when cancellation prevents confirming the loop has exited.
        if (_work?.IsCompleted == false) throw new InvalidOperationException("Synchronization supervision did not stop safely.");
        _lifetime = null;
        lifetime.Dispose();
        _work = null;
    }

    private void OnProfileChanged(object? sender, EventArgs args) => activity.RequestRefresh();
    private void OnRuntimeChanged(object? sender, SyncRuntimeStateChangedEventArgs args)
    {
        // A direct foreground refresh can fail outside this loop. Preserve its retry obligation.
        if (args.Current.IsRetryable && Volatile.Read(ref _insideReconciliation) == 0) activity.RequestRefresh();
    }

    private async Task RunAsync(CancellationToken ct)
    {
        var attempt = 0;
        using var reads = CancellationTokenSource.CreateLinkedTokenSource(ct);
        await using var reader = activity.ReadRefreshesAsync(reads.Token).GetAsyncEnumerator(reads.Token);
        Task<bool>? signal = null;
        try
        {
            while (!ct.IsCancellationRequested)
            {
                signal ??= reader.MoveNextAsync().AsTask();
                var snapshot = runtime.Snapshot;
                var delay = snapshot.RequiresProcessRestart ? Timeout.InfiniteTimeSpan
                    : snapshot.IsRetryable ? RetryDelay(attempt)
                    : snapshot.State == SyncRuntimeState.Running || snapshot.IsPassiveListening
                        ? HealthCheckInterval : Timeout.InfiniteTimeSpan;
                using var timerCancellation = CancellationTokenSource.CreateLinkedTokenSource(ct);
                var timer = Task.Delay(delay, _clock, timerCancellation.Token);
                var winner = await Task.WhenAny(signal, timer);
                if (winner == signal)
                {
                    timerCancellation.Cancel();
                    if (!await signal) break;
                    signal = null;
                    // One bounded debounce; a continuous stream cannot postpone the decision forever.
                    await Task.Delay(TimeSpan.FromMilliseconds(75), _clock, ct);
                }
                else
                {
                    await timer;
                    // The fallback checks process-local task health only. Healthy runtimes do
                    // no periodic SQLite reads or writes and never restart their services.
                    if (!snapshot.IsRetryable && runtime is ISyncRuntimeHealth health && health.IsHealthy) continue;
                }

                if (runtime.Snapshot.RequiresProcessRestart) continue;
                try
                {
                    Volatile.Write(ref _insideReconciliation, 1);
                    await runtime.RefreshSyncEnabledAsync(ct);
                    attempt = 0;
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested) { break; }
                catch (Exception ex)
                {
                    BackendDebugLog.Warning("Synchronization runtime reconciliation failed; safe recovery is scheduled when permitted.", ex, "Synchronization");
                    attempt = Math.Min(attempt + 1, 7);
                }
                finally { Volatile.Write(ref _insideReconciliation, 0); }
            }
        }
        finally
        {
            reads.Cancel();
            if (signal is not null)
            {
                try { await signal; } catch (OperationCanceledException) when (reads.IsCancellationRequested) { }
            }
        }
    }
}
