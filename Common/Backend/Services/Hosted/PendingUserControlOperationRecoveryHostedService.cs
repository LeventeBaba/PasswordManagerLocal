using Microsoft.Extensions.DependencyInjection;
using PasswordManagerLocal.Common.Backend.Abstractions.Repositories;
using PasswordManagerLocal.Common.Backend.Abstractions.Services;

namespace PasswordManagerLocal.Common.Backend.Services.Hosted;

/// <summary>
/// Re-applies control operations that were durably stored before a crash or cleanup failure. A
/// deletion remains StoredPending until its barrier and cleanup commit, so restart is fail-closed.
/// </summary>
public sealed class PendingUserControlOperationRecoveryHostedService : IBackendHostedService
{
    private readonly IServiceScopeFactory _scopeFactory;

    public PendingUserControlOperationRecoveryHostedService(IServiceScopeFactory scopeFactory) =>
        _scopeFactory = scopeFactory;

    public async Task StartAsync(CancellationToken cancellationToken = default)
    {
        await using var inventoryScope = _scopeFactory.CreateAsyncScope();
        var pending = await inventoryScope.ServiceProvider
            .GetRequiredService<IUserControlOperationRepository>()
            .ListPendingAsync(cancellationToken);

        foreach (var row in pending)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                await using var applyScope = _scopeFactory.CreateAsyncScope();
                await applyScope.ServiceProvider
                    .GetRequiredService<IUserControlOperationInboxService>()
                    .TryApplyStoredAsync(row.OperationId, cancellationToken);
            }
            catch when (!cancellationToken.IsCancellationRequested)
            {
                // Keep StoredPending. The next process start or a duplicate anti-entropy delivery
                // retries it; acknowledgement is never fabricated for incomplete cleanup.
            }
        }
    }

    public Task StopAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
}
