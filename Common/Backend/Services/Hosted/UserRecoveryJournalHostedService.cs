using Microsoft.Extensions.DependencyInjection;
using PasswordManagerLocal.Common.Backend.Abstractions.Services;
using PasswordManagerLocal.Common.Backend.Diagnostics;

namespace PasswordManagerLocal.Common.Backend.Services.Hosted;

public sealed class UserRecoveryJournalHostedService(IServiceScopeFactory scopes) : IBackendHostedService
{
    private readonly object _gate = new();
    private CancellationTokenSource? _lifetime;
    private Task? _work;

    public Task StartAsync(CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        lock (_gate)
        {
            if (_work is not null) return Task.CompletedTask;
            _lifetime = new CancellationTokenSource();
            _work = ExecuteAsync(_lifetime.Token);
        }
        return Task.CompletedTask;
    }

    public async Task StopAsync(CancellationToken ct = default)
    {
        CancellationTokenSource? lifetime; Task? work;
        lock (_gate) { lifetime = _lifetime; work = _work; _lifetime = null; _work = null; }
        if (lifetime is null || work is null) return;
        lifetime.Cancel();
        try { await work.WaitAsync(ct); }
        catch (OperationCanceledException) when (lifetime.IsCancellationRequested && !ct.IsCancellationRequested) { }
        finally { lifetime.Dispose(); }
    }

    private async Task ExecuteAsync(CancellationToken ct)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromMinutes(1));
        do
        {
            try
            {
                await using var scope = scopes.CreateAsyncScope();
                await scope.ServiceProvider.GetRequiredService<IUserRecoveryJournalService>().MaintainAsync(ct);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                BackendDebugLog.Warning("Recovery journal maintenance will be retried.", ex, "SelfDiagnostics");
            }
        } while (await timer.WaitForNextTickAsync(ct));
    }
}
