using PasswordManagerLocal.Common.Backend.Abstractions.Services;
using PasswordManagerLocal.Common.Backend.Diagnostics;
using PasswordManagerLocal.Common.Backend.Utils;

namespace PasswordManagerLocal.Common.Backend.Services.Hosted;

/// <summary>Reconciles post-commit delivery changes outside network/worker call stacks. Idle has no timer or writes.</summary>
public sealed class RevocationDeliveryRuntimeHostedService(IRevocationDeliveryActivity activity,
    ISyncRuntimeService runtime) : IBackendHostedService
{
    private CancellationTokenSource? _lifetime;
    private Task? _work;
    public Task StartAsync(CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        if (_lifetime is not null) return Task.CompletedTask;
        _lifetime = new();
        var token = _lifetime.Token;
        _work = IndependentBackgroundWork.Run(() => RunAsync(token), CancellationToken.None);
        activity.RequestRefresh();
        return Task.CompletedTask;
    }
    public async Task StopAsync(CancellationToken ct = default)
    {
        var lifetime = _lifetime;
        if (lifetime is null) return;
        _lifetime = null;
        lifetime.Cancel();
        try { if (_work is not null) await _work.WaitAsync(ct); }
        catch (OperationCanceledException) when (lifetime.IsCancellationRequested && !ct.IsCancellationRequested) { }
        finally { lifetime.Dispose(); _work = null; }
    }
    private async Task RunAsync(CancellationToken ct)
    {
        await foreach (var _ in activity.ReadRefreshesAsync(ct))
        {
            if (activity.HasActiveWork) continue; // The final worker/frame lease queues a fresh authoritative read.
            try { await runtime.RefreshSyncEnabledAsync(ct); }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { break; }
            catch (Exception ex)
            {
                BackendDebugLog.Warning("Delivery runtime reconciliation will retry.", ex, "Synchronization");
                await Task.Delay(TimeSpan.FromSeconds(1), ct);
                activity.RequestRefresh();
            }
        }
    }
}
