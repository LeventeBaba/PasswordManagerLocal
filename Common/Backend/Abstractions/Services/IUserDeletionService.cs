using PasswordManagerLocal.Common.Backend.Models;

namespace PasswordManagerLocal.Common.Backend.Abstractions.Services;

public interface IUserDeletionService
{
    Task DeleteUserAsync(User user, CancellationToken ct = default);
    Task DeleteUserAsync(User user, bool enqueueSync, CancellationToken ct = default);
    Task DeleteUserAsync(Guid uid, CancellationToken ct = default);
    Task DeleteUserAsync(Guid uid, bool enqueueSync, CancellationToken ct = default);
    Task DeleteUserByTokenAsync(Guid token, CancellationToken ct = default);
    Task DeleteUserByTokenAsync(Guid token, bool enqueueSync, CancellationToken ct = default);
}
