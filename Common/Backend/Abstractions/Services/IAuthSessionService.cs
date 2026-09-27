using PasswordManagerLocal.Common.Backend.Models;
using PasswordManagerLocal.Common.Contracts.Responses;

namespace PasswordManagerLocal.Common.Backend.Abstractions.Services;

public interface IAuthSessionService
{
    Task<Guid> RenewSessionAsync(Guid token, CancellationToken ct = default);
    void Logout(Guid token);
    void LogoutUser(Guid uid);
    void LogoutUser(Guid uid, AuthSessionInvalidationReason reason);
    AuthSessionStatusResponse GetSessionStatus(Guid token);
    Task RefreshSyncedUserSessionsAsync(User user, CancellationToken ct = default);
}
