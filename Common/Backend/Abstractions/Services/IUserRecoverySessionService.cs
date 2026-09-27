using PasswordManagerLocal.Common.Backend.Models;

namespace PasswordManagerLocal.Common.Backend.Abstractions.Services;

public interface IUserRecoverySessionService
{
    Task RefreshOrInvalidateAsync(User user, CancellationToken ct = default);
}
