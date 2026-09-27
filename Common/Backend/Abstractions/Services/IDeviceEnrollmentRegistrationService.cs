using PasswordManagerLocal.Common.Backend.Sync.Enrollment;

namespace PasswordManagerLocal.Common.Backend.Abstractions.Services;

public interface IDeviceEnrollmentRegistrationService
{
    Task RegisterRemoteDeviceAsync(IServiceProvider services, Guid userId, EnrollmentEndpoint endpoint, CancellationToken ct = default);
    Task RejectIfPrimaryUserAlreadyLinkedToLocalDeviceAsync(IServiceProvider services, Guid userId, CancellationToken ct = default);
    Task RegisterIncomingEnrollmentSourceEndpointAsync(IServiceProvider services, string sourceDeviceId, string sourceTlsCertFingerprint, string? sourceHost, CancellationToken ct = default);
    Task QueueInitialSyncAsync(IServiceProvider services, Guid userId, Guid newDeviceId, CancellationToken ct = default);
}
