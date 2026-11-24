using PasswordManagerLocal.Common.Backend.Models;
using PasswordManagerLocal.Common.Backend.Security;

namespace PasswordManagerLocal.Common.Backend.Abstractions.Services;

public interface IUserCanonicalHealthService
{
    Task<CanonicalHealthResult> VerifyAsync(
        User user,
        EncryptionKey? key,
        UserSyncKeyConfidence keyConfidence,
        bool recordFault,
        CancellationToken ct = default);

    Task<CanonicalHealthResult> VerifyCapturedAsync(User user, UserCanonicalCheckpoint? checkpoint,
        EncryptionKey? key, UserSyncKeyConfidence keyConfidence, CancellationToken ct = default) =>
        throw new NotSupportedException("Detached canonical verification is unavailable.");

    Task UpdateCheckpointAsync(User user, CancellationToken ct = default);
    Task DeleteCheckpointAsync(Guid userId, CancellationToken ct = default);
}
