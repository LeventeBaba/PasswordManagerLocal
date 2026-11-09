namespace PasswordManagerLocal.Common.Frontend.Services;

public sealed class TaskFrontendRefreshDelay : IFrontendRefreshDelay
{
    public Task DelayAsync(TimeSpan delay, CancellationToken cancellationToken) =>
        Task.Delay(delay, cancellationToken);
}
