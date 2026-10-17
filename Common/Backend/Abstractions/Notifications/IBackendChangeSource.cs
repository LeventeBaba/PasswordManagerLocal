namespace PasswordManagerLocal.Common.Backend.Abstractions.Notifications;

/// <summary>Provides bounded, runtime-scoped observation of backend change hints.</summary>
public interface IBackendChangeSource
{
    BackendChangePosition CurrentPosition { get; }

    /// <summary>
    /// Atomically captures the current watermark and registers a subscriber. A new subscriber
    /// must perform an authoritative read; matching changes published after the watermark are
    /// buffered. A null user ID subscribes to global events only.
    /// </summary>
    IBackendChangeSubscription Subscribe(Guid? userId = null, int capacity = 64);
}
