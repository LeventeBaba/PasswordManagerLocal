namespace PasswordManagerLocal.Common.Frontend.Services;

/// <summary>Debounce delay abstraction used to keep refresh coordination deterministic in tests.</summary>
public interface IFrontendRefreshDelay
{
    Task DelayAsync(TimeSpan delay, CancellationToken cancellationToken);
}
