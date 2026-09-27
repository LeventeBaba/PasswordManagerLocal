using PasswordManagerLocal.Common.Backend.Abstractions.Sync.Presence;
using PasswordManagerLocal.Common.Backend.Diagnostics;
using PasswordManagerLocal.Common.Backend.Sync.Discovery;
using PasswordManagerLocal.Common.Backend.Utils;

namespace PasswordManagerLocal.Common.Backend.Sync.Presence;

public sealed class DevicePresenceRegistry : IDevicePresenceRegistry
{
    internal const int FailureThreshold = 3;

    private readonly object _gate = new();
    private readonly Dictionary<string, Entry> _entries = new(StringComparer.OrdinalIgnoreCase);
    private readonly Func<DateTimeOffset> _utcNow;

    public DevicePresenceRegistry(TimeProvider timeProvider)
        : this(() => (timeProvider ?? throw new ArgumentNullException(nameof(timeProvider))).GetUtcNow())
    {
    }

    internal DevicePresenceRegistry(Func<DateTimeOffset> utcNow) =>
        _utcNow = utcNow ?? throw new ArgumentNullException(nameof(utcNow));

    public void RefreshAuthenticated(
        string tlsCertificateFingerprint,
        DiscoveredDeviceEndpoint? endpoint,
        DevicePresenceObservationSource source)
    {
        var fingerprint = Normalize(tlsCertificateFingerprint);
        if (fingerprint.Length == 0)
            return;

        var now = _utcNow();
        var endpointKey = BuildEndpointKey(endpoint);
        bool changedOnline;

        lock (_gate)
        {
            if (!_entries.TryGetValue(fingerprint, out var entry))
            {
                entry = new Entry();
                _entries[fingerprint] = entry;
            }

            changedOnline = !entry.IsOnline;
            entry.IsOnline = true;
            entry.LastAuthenticatedAt = now;
            entry.LastObservationSource = source;
            entry.ConsecutiveFailures = 0;
            if (endpointKey is not null)
                entry.EndpointKey = endpointKey;
        }

        BackendDebugLog.Debug(
            $"Authenticated device presence refreshed. FingerprintPrefix={Prefix(fingerprint)}, Source={source}, Endpoint={endpointKey ?? "connection-observed"}.",
            "Presence");
        if (changedOnline)
        {
            BackendDebugLog.Debug(
                $"Device presence changed to online. FingerprintPrefix={Prefix(fingerprint)}, Source={source}.",
                "Presence");
        }
    }

    public void RecordFailure(
        string tlsCertificateFingerprint,
        DiscoveredDeviceEndpoint? endpoint,
        DevicePresenceFailureKind failureKind)
    {
        var fingerprint = Normalize(tlsCertificateFingerprint);
        if (fingerprint.Length == 0)
            return;

        var endpointKey = BuildEndpointKey(endpoint);
        bool changedOffline = false;
        int failureCount;

        lock (_gate)
        {
            if (!_entries.TryGetValue(fingerprint, out var entry))
            {
                entry = new Entry();
                _entries[fingerprint] = entry;
            }

            if (entry.EndpointKey is not null &&
                endpointKey is not null &&
                !string.Equals(entry.EndpointKey, endpointKey, StringComparison.OrdinalIgnoreCase))
            {
                return;
            }

            entry.ConsecutiveFailures++;
            failureCount = entry.ConsecutiveFailures;
            if (entry.IsOnline &&
                (RequiresImmediateOffline(failureKind) || failureCount >= FailureThreshold))
            {
                entry.IsOnline = false;
                changedOffline = true;
            }
        }

        BackendDebugLog.DebugRateLimited(
            $"presence-failure:{fingerprint}:{failureKind}",
            TimeSpan.FromSeconds(5),
            $"Authenticated device presence probe failed. FingerprintPrefix={Prefix(fingerprint)}, Failure={failureKind}, ConsecutiveFailures={failureCount}, Endpoint={endpointKey ?? "none"}.",
            "Presence");

        if (changedOffline)
        {
            var reason = RequiresImmediateOffline(failureKind)
                ? "authentication or protocol validation failed"
                : "authenticated contact failed repeatedly";
            BackendDebugLog.Debug(
                $"Device presence changed to offline because {reason}. FingerprintPrefix={Prefix(fingerprint)}, Failure={failureKind}.",
                "Presence");
        }
    }

    public void HandleEndpointChanged(
        string tlsCertificateFingerprint,
        DiscoveredDeviceEndpoint endpoint)
    {
        ArgumentNullException.ThrowIfNull(endpoint);
        var fingerprint = Normalize(tlsCertificateFingerprint);
        var endpointKey = BuildEndpointKey(endpoint);
        if (fingerprint.Length == 0 || endpointKey is null)
            return;

        bool invalidated = false;
        string? previousEndpoint = null;
        lock (_gate)
        {
            if (!_entries.TryGetValue(fingerprint, out var entry))
                return;

            previousEndpoint = entry.EndpointKey;
            if (previousEndpoint is null ||
                string.Equals(previousEndpoint, endpointKey, StringComparison.OrdinalIgnoreCase))
            {
                return;
            }

            invalidated = entry.IsOnline || entry.LastAuthenticatedAt.HasValue;
            entry.IsOnline = false;
            entry.LastAuthenticatedAt = null;
            entry.LastObservationSource = null;
            entry.ConsecutiveFailures = 0;
            entry.EndpointKey = endpointKey;
        }

        if (invalidated)
        {
            BackendDebugLog.Debug(
                $"Device presence invalidated because the endpoint changed. FingerprintPrefix={Prefix(fingerprint)}, PreviousEndpoint={previousEndpoint}, NewEndpoint={endpointKey}.",
                "Presence");
        }
    }

    public bool IsOnline(string tlsCertificateFingerprint, TimeSpan maximumAge)
    {
        if (maximumAge <= TimeSpan.Zero)
            return false;

        var fingerprint = Normalize(tlsCertificateFingerprint);
        if (fingerprint.Length == 0)
            return false;

        bool expired = false;
        lock (_gate)
        {
            if (!_entries.TryGetValue(fingerprint, out var entry) ||
                !entry.IsOnline ||
                !entry.LastAuthenticatedAt.HasValue)
            {
                return false;
            }

            var age = _utcNow() - entry.LastAuthenticatedAt.Value;
            if (age >= TimeSpan.Zero && age <= maximumAge)
                return true;

            entry.IsOnline = false;
            expired = true;
        }

        if (expired)
        {
            BackendDebugLog.Debug(
                $"Authenticated device presence expired. FingerprintPrefix={Prefix(fingerprint)}, MaximumAgeSeconds={maximumAge.TotalSeconds:0.###}.",
                "Presence");
        }

        return false;
    }

    public int ExpireStale(TimeSpan maximumAge)
    {
        if (maximumAge <= TimeSpan.Zero)
            return 0;

        var now = _utcNow();
        var expired = new List<string>();
        lock (_gate)
        {
            foreach (var pair in _entries)
            {
                var entry = pair.Value;
                if (!entry.IsOnline || !entry.LastAuthenticatedAt.HasValue)
                    continue;

                var age = now - entry.LastAuthenticatedAt.Value;
                if (age < TimeSpan.Zero || age <= maximumAge)
                    continue;

                entry.IsOnline = false;
                expired.Add(pair.Key);
            }
        }

        foreach (var fingerprint in expired)
        {
            BackendDebugLog.Debug(
                $"Authenticated device presence expired. FingerprintPrefix={Prefix(fingerprint)}, MaximumAgeSeconds={maximumAge.TotalSeconds:0.###}.",
                "Presence");
        }

        return expired.Count;
    }

    public void InvalidateAll(string reason)
    {
        var changed = 0;
        lock (_gate)
        {
            foreach (var entry in _entries.Values)
            {
                if (entry.IsOnline || entry.LastAuthenticatedAt.HasValue)
                    changed++;

                entry.IsOnline = false;
                entry.LastAuthenticatedAt = null;
                entry.LastObservationSource = null;
                entry.ConsecutiveFailures = 0;
                entry.EndpointKey = null;
            }
        }

        BackendDebugLog.Debug(
            $"Authenticated device presence was invalidated. Reason={SanitizeReason(reason)}, AffectedEntries={changed}.",
            "Presence");
    }

    public bool TryGetSnapshot(
        string tlsCertificateFingerprint,
        out DevicePresenceSnapshot? snapshot)
    {
        var fingerprint = Normalize(tlsCertificateFingerprint);
        lock (_gate)
        {
            if (fingerprint.Length == 0 || !_entries.TryGetValue(fingerprint, out var entry))
            {
                snapshot = null;
                return false;
            }

            snapshot = new DevicePresenceSnapshot(
                fingerprint,
                entry.IsOnline,
                entry.LastAuthenticatedAt,
                entry.LastObservationSource,
                entry.ConsecutiveFailures,
                entry.EndpointKey);
            return true;
        }
    }


    private static bool RequiresImmediateOffline(DevicePresenceFailureKind failureKind) =>
        failureKind is
            DevicePresenceFailureKind.TlsOrFingerprintMismatch or
            DevicePresenceFailureKind.PeerIdentityMismatch or
            DevicePresenceFailureKind.Unauthorized or
            DevicePresenceFailureKind.ProtocolUnavailable or
            DevicePresenceFailureKind.InvalidResponse;

    private static string Normalize(string fingerprint) =>
        FingerprintUtil.NormalizeOrEmpty(fingerprint);

    private static string? BuildEndpointKey(DiscoveredDeviceEndpoint? endpoint)
    {
        if (endpoint is null || string.IsNullOrWhiteSpace(endpoint.Host) || endpoint.Port <= 0)
            return null;

        return $"{endpoint.Host.Trim()}:{endpoint.Port}";
    }

    private static string Prefix(string fingerprint) =>
        fingerprint[..Math.Min(16, fingerprint.Length)];

    private static string SanitizeReason(string reason) =>
        string.IsNullOrWhiteSpace(reason)
            ? "unspecified"
            : reason.Trim().Replace('\r', ' ').Replace('\n', ' ');

    private sealed class Entry
    {
        public bool IsOnline { get; set; }
        public DateTimeOffset? LastAuthenticatedAt { get; set; }
        public DevicePresenceObservationSource? LastObservationSource { get; set; }
        public int ConsecutiveFailures { get; set; }
        public string? EndpointKey { get; set; }
    }
}
