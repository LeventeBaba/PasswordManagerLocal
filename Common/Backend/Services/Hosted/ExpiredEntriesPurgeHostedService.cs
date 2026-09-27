using PasswordManagerLocal.Common.Backend.Abstractions.Services;
using static PasswordManagerLocal.Common.Backend.Constants.EntryExpirationConstants;

namespace PasswordManagerLocal.Common.Backend.Services.Hosted;

public sealed class ExpiredEntriesPurgeHostedService : IInteractiveBackendHostedService
{
    private readonly ITokenService _tokens;
    private readonly IKeyVaultService _keys;
    private readonly object _lifecycleLock = new();
    private CancellationTokenSource? _lifetimeCancellation;
    private Task? _executeTask;

    public ExpiredEntriesPurgeHostedService(ITokenService tokens, IKeyVaultService keys)
    {
        _tokens = tokens;
        _keys = keys;
    }

    public Task StartAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        lock (_lifecycleLock)
        {
            if (_executeTask is not null)
                return Task.CompletedTask;

            _lifetimeCancellation = new CancellationTokenSource();
            _executeTask = ExecuteAsync(_lifetimeCancellation.Token);
        }

        return Task.CompletedTask;
    }

    public async Task StopAsync(CancellationToken cancellationToken = default)
    {
        CancellationTokenSource? lifetimeCancellation;
        Task? executeTask;

        lock (_lifecycleLock)
        {
            lifetimeCancellation = _lifetimeCancellation;
            executeTask = _executeTask;
            _lifetimeCancellation = null;
            _executeTask = null;
        }

        if (lifetimeCancellation is null || executeTask is null)
            return;

        lifetimeCancellation.Cancel();

        try
        {
            await executeTask.WaitAsync(cancellationToken);
        }
        catch (OperationCanceledException) when (lifetimeCancellation.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
        {
        }
        finally
        {
            lifetimeCancellation.Dispose();
        }
    }

    private async Task ExecuteAsync(CancellationToken cancellationToken)
    {
        using var timer = new PeriodicTimer(PurgeExpiredPeriod);

        while (await timer.WaitForNextTickAsync(cancellationToken))
        {
            _tokens.PurgeExpired();
            _keys.PurgeExpired();
        }
    }
}
