using PasswordManagerLocal.Common.Backend.Internal.Recovery;

namespace PasswordManagerLocal.Common.Backend.Abstractions.Persistence;

public interface IUserCanonicalRecoveryStore
{
    Task<UserRecoveryInput> CaptureAsync(Guid userId, CancellationToken ct = default);
    // Requires the same session's immediate write transaction and no speculative mutations.
    Task<bool> IsCurrentAsync(UserRecoveryInput input, CancellationToken ct = default);
}
