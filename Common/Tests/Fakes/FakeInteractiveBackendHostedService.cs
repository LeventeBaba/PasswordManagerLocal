using PasswordManagerLocal.Common.Backend.Abstractions.Services;

namespace PasswordManagerLocal.Common.Tests.Fakes;

public sealed class FakeInteractiveBackendHostedService : IInteractiveBackendHostedService
{
    public int StartCalls { get; private set; }
    public int StopCalls { get; private set; }
    public Exception? StartFailure { get; set; }
    public Exception? StopFailure { get; set; }
    public Task? StartBlock { get; set; }

    public async Task StartAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        StartCalls++;
        if (StartBlock is not null)
            await StartBlock.WaitAsync(cancellationToken);
        if (StartFailure is not null)
            throw StartFailure;
    }

    public Task StopAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        StopCalls++;
        return StopFailure is null
            ? Task.CompletedTask
            : Task.FromException(StopFailure);
    }
}
