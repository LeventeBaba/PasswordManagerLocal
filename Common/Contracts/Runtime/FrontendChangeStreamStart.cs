namespace PasswordManagerLocal.Common.Contracts.Runtime;

/// <summary>
/// The atomic watermark captured when a frontend change stream is registered.
/// A new attachment always needs an authoritative reconciliation because no history is kept.
/// </summary>
public sealed record FrontendChangeStreamStart(
    Guid RuntimeInstanceId,
    long Sequence,
    bool RequiresAuthoritativeRefresh);
