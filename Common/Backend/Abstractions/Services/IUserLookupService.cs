using PasswordManagerLocal.Common.Backend.Models;

namespace PasswordManagerLocal.Common.Backend.Abstractions.Services;

public interface IUserLookupService
{
    Task<User?> GetUserByUidAsync(Guid uid, CancellationToken ct = default);
    Task<User> GetAndVerifyUserByUidAsync(Guid uid, CancellationToken ct = default);
    Task<User> GetAndVerifyUserAsync(Guid token, CancellationToken ct = default);
    Task<UserLoginIdentityMatchResult> ResolveUsernameAsync(byte[] username, CancellationToken ct = default);
    Task<User?> GetUserByUsernameAsync(byte[] username, CancellationToken ct = default);
    Task<User> GetAndVerifyUserByUsernameAsync(byte[] username, CancellationToken ct = default);
    Task<IReadOnlyList<User>> GetAndVerifyRememberMeEnabledUsersAsync(CancellationToken ct = default);
    Task<bool> UserExistsAsync(Guid uid, CancellationToken ct = default);
}
