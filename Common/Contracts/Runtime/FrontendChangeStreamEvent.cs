using PasswordManagerLocal.Common.Contracts.Notifications;

namespace PasswordManagerLocal.Common.Contracts.Runtime;

/// <summary>
/// Runtime-client event stream value. It contains invalidation metadata only and never identifies
/// the user whose backend event caused it.
/// </summary>
public sealed record FrontendChangeStreamEvent(
    FrontendChangeStreamEventKind Kind,
    Guid? RuntimeInstanceId,
    long Sequence,
    FrontendChangeScope Scope,
    bool RequiresAuthoritativeRefresh);
