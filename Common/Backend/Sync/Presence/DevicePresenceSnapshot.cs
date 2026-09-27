namespace PasswordManagerLocal.Common.Backend.Sync.Presence;

public sealed record DevicePresenceSnapshot(
    string TlsCertificateFingerprint,
    bool IsOnline,
    DateTimeOffset? LastAuthenticatedAt,
    DevicePresenceObservationSource? LastObservationSource,
    int ConsecutiveFailures,
    string? EndpointKey);
