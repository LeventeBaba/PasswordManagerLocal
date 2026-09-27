namespace PasswordManagerLocal.Common.Backend.Sync.Presence;

public enum DevicePresenceFailureKind
{
    None = 0,
    EndpointUnavailable = 1,
    Unreachable = 2,
    Timeout = 3,
    TlsOrFingerprintMismatch = 4,
    PeerIdentityMismatch = 5,
    Unauthorized = 6,
    ProtocolUnavailable = 7,
    InvalidResponse = 8
}
