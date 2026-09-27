using PasswordManagerLocal.Common.Backend.Abstractions.Sync.Presence;
using PasswordManagerLocal.Common.Backend.Sync.Discovery;
using PasswordManagerLocal.Common.Backend.Sync.Presence;

namespace PasswordManagerLocal.Common.Tests.Fakes;

public sealed class RecordingDevicePresenceRegistry : IDevicePresenceRegistry
{
    public bool IsOnlineResult { get; set; }
    public TimeSpan? LastMaximumAge { get; private set; }
    public string? LastFingerprint { get; private set; }

    public void RefreshAuthenticated(string tlsCertificateFingerprint, DiscoveredDeviceEndpoint? endpoint, DevicePresenceObservationSource source)
    {
    }

    public void RecordFailure(string tlsCertificateFingerprint, DiscoveredDeviceEndpoint? endpoint, DevicePresenceFailureKind failureKind)
    {
    }

    public void HandleEndpointChanged(string tlsCertificateFingerprint, DiscoveredDeviceEndpoint endpoint)
    {
    }

    public bool IsOnline(string tlsCertificateFingerprint, TimeSpan maximumAge)
    {
        LastFingerprint = tlsCertificateFingerprint;
        LastMaximumAge = maximumAge;
        return IsOnlineResult;
    }

    public int ExpireStale(TimeSpan maximumAge) => 0;

    public void InvalidateAll(string reason)
    {
    }

    public bool TryGetSnapshot(string tlsCertificateFingerprint, out DevicePresenceSnapshot? snapshot)
    {
        snapshot = null;
        return false;
    }
}
