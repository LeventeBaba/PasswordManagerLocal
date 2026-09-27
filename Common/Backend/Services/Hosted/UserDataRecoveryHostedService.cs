using Microsoft.Extensions.DependencyInjection;
using PasswordManagerLocal.Common.Backend.Abstractions.Repositories;
using PasswordManagerLocal.Common.Backend.Abstractions.Services;
using PasswordManagerLocal.Common.Backend.Models;
using PasswordManagerLocal.Common.Backend.Services;

namespace PasswordManagerLocal.Common.Backend.Services.Hosted;

public sealed class UserDataRecoveryHostedService : IBackendHostedService
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly UserDataRecoveryScheduler _scheduler;
    private readonly object _gate = new();
    private CancellationTokenSource? _lifetime;
    private Task? _executeTask;

    public UserDataRecoveryHostedService(
        IServiceScopeFactory scopeFactory,
        UserDataRecoveryScheduler scheduler)
    {
        _scopeFactory = scopeFactory;
        _scheduler = scheduler;
    }

    public async Task StartAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (_gate)
        {
            if (_executeTask is not null)
                return;
            _lifetime = new CancellationTokenSource();
            _executeTask = Task.WhenAll(
                ExecuteAsync(_lifetime.Token),
                PeriodicScanAsync(_lifetime.Token));
        }

        await ScheduleDueFaultsAsync(UserDataRecoveryTrigger.StartupRecoveryScan, cancellationToken);
    }

    public async Task StopAsync(CancellationToken cancellationToken = default)
    {
        CancellationTokenSource? lifetime;
        Task? executeTask;
        lock (_gate)
        {
            lifetime = _lifetime;
            executeTask = _executeTask;
            _lifetime = null;
            _executeTask = null;
        }

        if (lifetime is null || executeTask is null)
            return;
        lifetime.Cancel();
        try
        {
            await executeTask.WaitAsync(cancellationToken);
        }
        catch (OperationCanceledException) when (lifetime.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
        {
        }
        finally
        {
            lifetime.Dispose();
        }
    }

    private async Task PeriodicScanAsync(CancellationToken ct)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromMinutes(5));
        while (await timer.WaitForNextTickAsync(ct))
            await ScheduleDueFaultsAsync(UserDataRecoveryTrigger.PeriodicMaintenance, ct);
    }

    private async Task ScheduleDueFaultsAsync(
        UserDataRecoveryTrigger trigger,
        CancellationToken ct)
    {
        await using var scope = _scopeFactory.CreateAsyncScope();
        var faults = await scope.ServiceProvider
            .GetRequiredService<IUserSyncFaultRepository>()
            .ListRecoverableLocalCanonicalAsync(DateTimeOffset.UtcNow, ct);
        foreach (var fault in faults)
            _scheduler.Schedule(fault.UserId, trigger);
    }

    private async Task ExecuteAsync(CancellationToken ct)
    {
        await foreach (var item in _scheduler.ReadAllAsync(ct))
        {
            _scheduler.BeginProcessing(item.UserId);
            try
            {
                await using var scope = _scopeFactory.CreateAsyncScope();
                var users = scope.ServiceProvider.GetRequiredService<IUserRepository>();
                var user = await users.GetByIdAsync(item.UserId, ct);
                if (user is null)
                    continue;

                var resolver = scope.ServiceProvider.GetRequiredService<IUserSyncKeyResolverService>();
                if (!resolver.TryResolve(user, out var key, out var confidence) || key is null)
                    continue;

                using (key)
                {
                    await scope.ServiceProvider.GetRequiredService<IUserDataRecoveryCoordinator>()
                        .TryRecoverAsync(item.UserId, key, confidence, item.Trigger, ct);
                }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch
            {
                // Durable fault state and backoff remain the source of truth.
            }
            finally
            {
                // BeginProcessing deliberately removed the queued marker. A new trigger that
                // arrived during this attempt is already coalesced in the channel.
            }
        }
    }
}
