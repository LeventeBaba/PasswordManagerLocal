using PasswordManagerLocal.Common.Backend.Abstractions.Services;
using PasswordManagerLocal.Common.Backend.Sync;
using PasswordManagerLocal.Common.Backend.Sync.Enrollment;

namespace PasswordManagerLocal.Common.Tests.Fakes;

public sealed class FakeLocalDiscoveryService : ILocalDiscoveryService
{
    public int ActivateCalls { get; private set; }
    public int DeactivateCalls { get; private set; }
    public string? ActiveSessionId { get; private set; }
    public IReadOnlyList<EnrollmentEndpoint> EnrollmentEndpoints { get; set; } = [];

    public void ActivateEnrollmentSession(string sessionId, byte[] secret, DateTimeOffset expiresAt)
    {
        ActivateCalls++;
        ActiveSessionId = sessionId;
    }

    public void DeactivateEnrollmentSession(string sessionId)
    {
        DeactivateCalls++;
        if (string.Equals(ActiveSessionId, sessionId, StringComparison.Ordinal))
            ActiveSessionId = null;
    }

    public Task<IReadOnlyList<EnrollmentEndpoint>> FindEnrollmentEndpointsAsync(DeviceEnrollmentParsedCode parsed, CancellationToken ct = default) =>
        Task.FromResult(EnrollmentEndpoints);
}
