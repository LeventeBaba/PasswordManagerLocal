using PasswordManagerLocal.Common.Backend.Models;
using PasswordManagerLocal.Common.Backend.Security;

namespace PasswordManagerLocal.Common.Backend.Abstractions.Services;

public interface IUserTombstoneGarbageCollector
{
    Task<TombstoneGarbageCollectionResult> CollectAsync(Guid userId, CancellationToken ct = default);
    Task<TombstoneGarbageCollectionResult> CollectAsync(Guid userId, EncryptionKey key, CancellationToken ct = default);
}
