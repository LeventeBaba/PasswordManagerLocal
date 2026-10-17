using Avalonia.Threading;

namespace PasswordManagerLocal.Common.Frontend.Services;

/// <summary>Dispatcher-bound one-shot timer used for advisory frontend deadlines.</summary>
public sealed class AvaloniaFrontendDeadlineScheduler : IFrontendDeadlineScheduler
{
    public IDisposable Schedule(DateTimeOffset deadlineUtc, Action callback)
    {
        ArgumentNullException.ThrowIfNull(callback);
        var delay = deadlineUtc.ToUniversalTime() - DateTimeOffset.UtcNow;
        if (delay < TimeSpan.Zero)
            delay = TimeSpan.Zero;

        return DispatcherTimer.RunOnce(callback, delay);
    }
}
