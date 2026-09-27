using PasswordManagerLocal.Windows.EndpointRpc.Client;

namespace PasswordManagerLocal.Windows.Tests.IPC.Infrastructure;

internal sealed class FakeIntentionalAgentShutdownCoordinator : IIntentionalAgentShutdownCoordinator
{
    public bool BeginResult { get; set; } = true;
    public int BeginCount { get; private set; }
    public int CancelCount { get; private set; }
    public Exception? BeginFailure { get; set; }

    public Task<bool> BeginIntentionalAgentShutdownAsync(
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        BeginCount++;
        return BeginFailure is null
            ? Task.FromResult(BeginResult)
            : Task.FromException<bool>(BeginFailure);
    }

    public Task CancelIntentionalAgentShutdownAsync(
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        CancelCount++;
        return Task.CompletedTask;
    }
}
