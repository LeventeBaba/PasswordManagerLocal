using PasswordManagerLocal.Common.Backend.Models;
using PasswordManagerLocal.Common.Backend.Sync;

namespace PasswordManagerLocal.Common.Backend.Abstractions.Services;

public interface IUserForkRecoveryService
{
    // No mutation, signing, membership grant, or enrollment occurs during assessment.
    Task<DeviceRemovalForkProof> PrepareRemovalAsync(User user, Guid selectedRemoteDeviceId, CancellationToken ct = default);
    Task<bool> CanAccessIndependentCanonicalAsync(User user, CancellationToken ct = default);
    // Caller owns the signed-removal transaction. Proof is evaluated before changing canonical epochs.
    Task StageSignedExclusionAsync(User user, DeviceRemovalPayload payload, UserControlOperationEnvelope operation, CancellationToken ct = default);
}
