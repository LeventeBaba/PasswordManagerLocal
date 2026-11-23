using PasswordManagerLocal.Common.Backend.Models;

namespace PasswordManagerLocal.Common.Backend.Abstractions.Persistence;

/// <summary>Removes mutable profile state inside an authenticated deletion or local-erasure transaction.</summary>
public interface IUserAccountDeletionStore
{
    Task DeleteDependentStateAsync(Guid userId, CancellationToken ct);
    // Explicit local erasure retains public fork/fault evidence and revision knowledge.
    Task DeleteLocalProfileStateAsync(Guid userId, CancellationToken ct);
    void StageUserDelete(User user);
}
