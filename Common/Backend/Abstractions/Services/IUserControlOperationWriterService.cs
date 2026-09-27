using PasswordManagerLocal.Common.Backend.Models;
using PasswordManagerLocal.Common.Backend.Sync;
using PasswordManagerLocal.Common.Backend.Sync.Enrollment;

namespace PasswordManagerLocal.Common.Backend.Abstractions.Services;

public interface IUserControlOperationWriterService
{
    Task<UserControlOperationEnvelope> CreateAppliedKeyEpochReplacementAsync(User resultingCanonicalUser, long previousKeyEpoch, CancellationToken ct = default);
    Task<UserControlOperationEnvelope> CreateAppliedDeviceAdditionAsync(User canonicalUser, EnrollmentEndpoint target, CancellationToken ct = default);
    Task<UserControlOperationEnvelope> CreateAppliedDeviceAdditionUnderLifecycleAsync(User canonicalUser, EnrollmentEndpoint target, CancellationToken ct = default);
    Task<UserControlOperationEnvelope> CreateAppliedDeviceRemovalAsync(User canonicalUser, DeviceRemovalPayload payload, CancellationToken ct = default);
    Task<UserControlOperationEnvelope> CreateAppliedDeviceRemovalUnderLifecycleAsync(User canonicalUser, DeviceRemovalPayload payload, CancellationToken ct = default);
    Task<UserControlOperationEnvelope> CreateAppliedAccountDeletionUnderLifecycleAsync(User canonicalUser, CancellationToken ct = default);
}
