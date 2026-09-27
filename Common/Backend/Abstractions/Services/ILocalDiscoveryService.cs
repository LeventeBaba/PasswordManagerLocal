using PasswordManagerLocal.Common.Backend.Sync;
using PasswordManagerLocal.Common.Backend.Sync.Enrollment;

namespace PasswordManagerLocal.Common.Backend.Abstractions.Services;

public interface ILocalDiscoveryService
{
    void ActivateEnrollmentSession(string sessionId, byte[] secret, DateTimeOffset expiresAt);
    void DeactivateEnrollmentSession(string sessionId);
    Task<IReadOnlyList<EnrollmentEndpoint>> FindEnrollmentEndpointsAsync(DeviceEnrollmentParsedCode parsed, CancellationToken ct = default);
}
