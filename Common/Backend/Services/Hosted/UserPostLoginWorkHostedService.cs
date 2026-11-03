using Microsoft.Extensions.DependencyInjection;
using PasswordManagerLocal.Common.Backend.Abstractions.Notifications;
using PasswordManagerLocal.Common.Backend.Abstractions.Repositories;
using PasswordManagerLocal.Common.Backend.Abstractions.Services;
using PasswordManagerLocal.Common.Backend.Diagnostics;
using PasswordManagerLocal.Common.Backend.Models;
using PasswordManagerLocal.Common.Contracts.Notifications;

namespace PasswordManagerLocal.Common.Backend.Services.Hosted;

/// <summary>Runtime-owned reconciliation; request ExecutionContext and scoped services never escape login.</summary>
public sealed class UserPostLoginWorkHostedService(
    IServiceScopeFactory scopes, UserPostLoginWorkScheduler scheduler, TimeProvider time) : IBackendHostedService
{
    private readonly object _gate = new();
    private CancellationTokenSource? _lifetime;
    private Task? _work;

    public Task StartAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (_gate)
        {
            if (_work is not null) return Task.CompletedTask;
            _lifetime = new CancellationTokenSource();
            var token = _lifetime.Token;
            // UserLifecycleCoordinator is AsyncLocal/reentrant. Never inherit a caller's lock ownership.
            using (ExecutionContext.SuppressFlow())
                _work = Task.Run(() => ExecuteAsync(token), CancellationToken.None);
        }
        return Task.CompletedTask;
    }

    public async Task StopAsync(CancellationToken cancellationToken = default)
    {
        CancellationTokenSource? lifetime;
        Task? work;
        lock (_gate) { lifetime = _lifetime; work = _work; }
        if (lifetime is null || work is null) return;
        await lifetime.CancelAsync();
        try { await work.WaitAsync(cancellationToken); }
        catch (OperationCanceledException) when (lifetime.IsCancellationRequested && !cancellationToken.IsCancellationRequested) { }
        finally
        {
            // Do not dispose running work/scopes if the caller stops waiting for shutdown.
            if (work.IsCompleted)
            {
                lock (_gate)
                {
                    if (ReferenceEquals(_work, work)) { _work = null; _lifetime = null; }
                }
                scheduler.Clear();
                lifetime.Dispose();
            }
        }
    }

    private async Task ExecuteAsync(CancellationToken ct)
    {
        var nextScan = time.GetUtcNow() + TimeSpan.FromMinutes(1);
        while (true)
        {
            ct.ThrowIfCancellationRequested();
            if (time.GetUtcNow() >= nextScan)
            {
                try { await ScanPendingAsync(ct); }
                catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
                catch (Exception ex) { LogDeferred(ex); }
                nextScan = time.GetUtcNow() + TimeSpan.FromMinutes(1);
            }
            while (scheduler.TryTakeReady() is { } item)
            {
                var retry = false;
                try
                {
                    using var lifetime = CancellationTokenSource.CreateLinkedTokenSource(ct, item.Cancellation.Token);
                    await ProcessAsync(item, lifetime.Token);
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested || item.Cancellation.IsCancellationRequested) { }
                catch (OperationCanceledException) { /* Interactive lifetime ended; durable work survives. */ }
                catch (Exception ex) { retry = true; LogDeferred(ex); }
                finally { scheduler.Complete(item, retry); }
                ct.ThrowIfCancellationRequested();
            }
            await scheduler.WaitForWorkAsync(nextScan - time.GetUtcNow(), ct);
        }
    }

    private async Task ScanPendingAsync(CancellationToken ct)
    {
        await using var scope = scopes.CreateAsyncScope();
        var users = scope.ServiceProvider.GetRequiredService<IUserRepository>();
        var snapshots = scope.ServiceProvider.GetRequiredService<IUserSyncSnapshotRepository>();
        foreach (var userId in await users.ListUserIdsAsync(ct))
        {
            var user = await users.GetByIdAsNoTrackingAsync(userId, ct);
            if (user is not null && (await snapshots.ListPendingForKeyEpochAsync(userId, user.KeyEpoch, ct)).Count != 0)
                scheduler.Schedule(userId);
        }
    }

    internal async Task ProcessAsync(UserPostLoginWorkScheduler.Work item, CancellationToken ct)
    {
        await using var scope = scopes.CreateAsyncScope();
        var services = scope.ServiceProvider;
        var lifecycle = services.GetRequiredService<IUserLifecycleCoordinator>();
        await lifecycle.ExecuteAsync(item.UserId, async innerCt =>
        {
            var tokens = services.GetRequiredService<ITokenService>();
            if (item.Token is { } session && (!tokens.TryGetUid(session, out var uid) || uid != item.UserId))
                return;
            var remaining = Timeout.InfiniteTimeSpan;
            if (item.Token is { } expiringSession)
            {
                if (!tokens.TryGetExpiresAtUtc(expiringSession, out var expires)) return;
                remaining = expires - time.GetUtcNow();
                if (remaining <= TimeSpan.Zero) return;
            }
            using var expiry = new CancellationTokenSource(remaining, time);
            using var sessionLifetime = CancellationTokenSource.CreateLinkedTokenSource(innerCt, expiry.Token);
            innerCt = sessionLifetime.Token;
            var users = services.GetRequiredService<IUserRepository>();
            var user = await users.GetByIdAsNoTrackingAsync(item.UserId, innerCt);
            if (user is null || (item.KeyEpoch.HasValue && item.KeyEpoch != user.KeyEpoch) ||
                (item.MembershipEpoch.HasValue && item.MembershipEpoch != user.MembershipEpoch))
                return;

            var interactive = services.GetRequiredService<IInteractiveSessionStateService>();
            try { await services.GetRequiredService<IUserLoginPreparationService>()
                .EnsureAuthorizedAsync(item.UserId, innerCt, requireCanonicalGeneration: false); }
            catch (UnauthorizedAccessException)
            {
                await interactive.LogoutUserAsync(item.UserId, AuthSessionInvalidationReason.LocalDataVerificationFailed, innerCt);
                return;
            }
            var resolver = services.GetRequiredService<IUserSyncKeyResolverService>();
            if (!resolver.TryResolve(user, out var key, out var confidence) || key is null)
                return;
            using (key)
            {
                var interactiveLifetime = confidence == UserSyncKeyConfidence.AuthenticatedSession
                    ? services.GetRequiredService<IInteractiveSessionLifetime>().CurrentLifetime
                    : CancellationToken.None;
                using var active = CancellationTokenSource.CreateLinkedTokenSource(innerCt, interactiveLifetime);
                var workCt = active.Token;
                workCt.ThrowIfCancellationRequested();
                try
                {
                    await services.GetRequiredService<IUserSnapshotMergeCoordinator>()
                        .TryMergePendingUnderLifecycleAsync(item.UserId, key, confidence, workCt);
                    await services.GetRequiredService<IUserDataRecoveryCoordinator>()
                        .TryRecoverAsync(item.UserId, key, confidence, UserDataRecoveryTrigger.PeriodicMaintenance, workCt);
                    try { await services.GetRequiredService<IUserTombstoneGarbageCollector>().CollectAsync(item.UserId, key, workCt); }
                    catch (Exception ex) when (ex is not OperationCanceledException) { LogDeferred(ex); }
                }
                finally
                {
                    if (!workCt.IsCancellationRequested)
                    {
                        // A deterministic conflict discovered by reconciliation can forbid login
                        // even when the pre-existing canonical bundle remains decryptable.
                        try { await services.GetRequiredService<IUserLoginPreparationService>().EnsureAuthorizedAsync(item.UserId, workCt); }
                        catch (Exception ex) when (ex is UnauthorizedAccessException or InvalidDataIntegrityException)
                        {
                            await interactive.LogoutUserAsync(item.UserId, AuthSessionInvalidationReason.LocalDataVerificationFailed, workCt);
                            throw;
                        }
                        var current = await users.GetByIdAsNoTrackingAsync(item.UserId, workCt);
                        if (current is not null)
                        {
                            await interactive.RefreshSyncedUserSessionsAsync(current, workCt);
                            // Publish after cache refresh, even if merge published an earlier notification.
                            services.GetService<IBackendChangePublisher>()?.Publish(
                                FrontendChangeScope.AllAuthenticatedData | FrontendChangeScope.AuthenticationSessions, item.UserId);
                        }
                    }
                }
            }
        }, ct);
    }

    private static void LogDeferred(Exception ex) => BackendDebugLog.Warning(
        $"Post-login reconciliation deferred. Failure={ex.GetType().Name}.", category: "Authentication");
}
