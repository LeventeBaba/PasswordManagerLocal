using PasswordManagerLocal.Common.Backend.Sync;
using PasswordManagerLocal.Common.Backend.Sync.Enrollment;

namespace PasswordManagerLocal.Common.Backend.Abstractions.Services;

public interface IDeviceEnrollmentEndpointService
{
    DeviceEnrollmentDirectEndpointInfo BuildDirectEndpointInfo();
    Task VerifyLocalEnrollmentListenerAsync(string sessionId, byte[] secret, DeviceEnrollmentDirectEndpointInfo endpointInfo, CancellationToken ct = default);
    EnrollmentEndpoint ToEnrollmentEndpoint(DeviceEnrollmentParsedDirectEndpoint endpoint);
    int GetDirectEndpointPriorityForThisDevice(EnrollmentEndpoint endpoint);
    bool IsLocalEndpoint(EnrollmentEndpoint endpoint);
    bool IsLocalDeviceIdentity(Guid deviceId, byte[] signPublicKey, string tlsCertFingerprint);
    bool FingerprintMatchesLocalDevice(string tlsCertFingerprint);
    Task<EnrollmentEndpoint> ResolveEndpointIdentityAsync(EnrollmentEndpoint endpoint, DeviceEnrollmentParsedCode parsed, CancellationToken ct = default);
}
