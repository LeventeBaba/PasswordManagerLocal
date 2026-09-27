using PasswordManagerLocal.Common.Backend.Abstractions.Sync.Discovery;
using PasswordManagerLocal.Common.Backend.Utils;
using System.Collections.Concurrent;

namespace PasswordManagerLocal.Common.Backend.Sync.Discovery;

public sealed class DiscoveredDeviceEndpointRegistry : IDiscoveredDeviceEndpointRegistry
{
    private readonly ConcurrentDictionary<
        string,
        (DiscoveredDeviceEndpoint Endpoint, DateTimeOffset ObservedAt)> _endpointsByFingerprint =
            new(StringComparer.OrdinalIgnoreCase);
    private readonly Func<DateTimeOffset> _utcNow;

    public DiscoveredDeviceEndpointRegistry()
        : this(static () => DateTimeOffset.UtcNow)
    {
    }

    internal DiscoveredDeviceEndpointRegistry(Func<DateTimeOffset> utcNow)
    {
        _utcNow = utcNow ?? throw new ArgumentNullException(nameof(utcNow));
    }

    public void AddOrUpdate(DiscoveredDeviceEndpoint endpoint)
    {
        if (endpoint is null)
            return;

        var fingerprint = FingerprintUtil.NormalizeOrEmpty(endpoint.TlsCertFingerprint);
        if (fingerprint.Length == 0 || string.IsNullOrWhiteSpace(endpoint.Host) || endpoint.Port <= 0)
            return;

        _endpointsByFingerprint[fingerprint] = (Clone(endpoint), _utcNow());
    }

    public bool TryGetByFingerprint(string tlsFingerprint, out DiscoveredDeviceEndpoint? endpoint)
    {
        var fingerprint = FingerprintUtil.NormalizeOrEmpty(tlsFingerprint);
        if (fingerprint.Length == 0 || !_endpointsByFingerprint.TryGetValue(fingerprint, out var registeredEndpoint))
        {
            endpoint = null;
            return false;
        }

        endpoint = Clone(registeredEndpoint.Endpoint);
        return true;
    }

    public bool IsRecentlyDiscovered(string tlsFingerprint, TimeSpan maximumAge)
    {
        if (maximumAge <= TimeSpan.Zero)
            return false;

        var fingerprint = FingerprintUtil.NormalizeOrEmpty(tlsFingerprint);
        if (fingerprint.Length == 0 || !_endpointsByFingerprint.TryGetValue(fingerprint, out var registeredEndpoint))
            return false;

        var age = _utcNow() - registeredEndpoint.ObservedAt;
        return age >= TimeSpan.Zero && age <= maximumAge;
    }

    public bool TryRemove(string tlsFingerprint)
    {
        var fingerprint = FingerprintUtil.NormalizeOrEmpty(tlsFingerprint);
        return fingerprint.Length != 0 && _endpointsByFingerprint.TryRemove(fingerprint, out _);
    }

    public void Clear() =>
        _endpointsByFingerprint.Clear();

    private static DiscoveredDeviceEndpoint Clone(DiscoveredDeviceEndpoint endpoint) =>
        new()
        {
            Host = endpoint.Host,
            Port = endpoint.Port,
            TlsCertFingerprint = endpoint.TlsCertFingerprint
        };
}
