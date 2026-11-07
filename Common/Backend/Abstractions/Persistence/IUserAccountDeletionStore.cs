using PasswordManagerLocal.Common.Backend.Models;

namespace PasswordManagerLocal.Common.Backend.Abstractions.Persistence;

/// <summary>Removes mutable account state within the authoritative deletion transaction.</summary>
public interface IUserAccountDeletionStore
{
    Task DeleteDependentStateAsync(Guid userId, CancellationToken ct);
    void StageUserDelete(User user);
}
