using PasswordManagerLocal.Common.Backend.Sync.Discovery;
using PasswordManagerLocal.Common.Backend.Sync.Presence;

namespace PasswordManagerLocal.Common.Backend.Abstractions.Sync.Presence;

public interface IDevicePresenceRegistry
{
    void RefreshAuthenticated(
        string tlsCertificateFingerprint,
        DiscoveredDeviceEndpoint? endpoint,
        DevicePresenceObservationSource source);

    void RecordFailure(
        string tlsCertificateFingerprint,
        DiscoveredDeviceEndpoint? endpoint,
        DevicePresenceFailureKind failureKind);

    void HandleEndpointChanged(
        string tlsCertificateFingerprint,
        DiscoveredDeviceEndpoint endpoint);

    bool IsOnline(string tlsCertificateFingerprint, TimeSpan maximumAge);

    int ExpireStale(TimeSpan maximumAge);

    void InvalidateAll(string reason);

    bool TryGetSnapshot(
        string tlsCertificateFingerprint,
        out DevicePresenceSnapshot? snapshot);
}
