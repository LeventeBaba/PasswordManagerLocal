using PasswordManagerLocal.Common.Backend.Abstractions.Repositories;
using PasswordManagerLocal.Common.Backend.Abstractions.Services;
using PasswordManagerLocal.Common.Backend.Exceptions;
using PasswordManagerLocal.Common.Backend.Models;

namespace PasswordManagerLocal.Common.Backend.Services;

/// <summary>
/// Loads and integrity-verifies user records without decrypting the encrypted user-data aggregate.
/// </summary>
public sealed class UserLookupService : IUserLookupService
{
    private readonly IUserRepository _users;
    private readonly IInteractiveUserDataStateAccessor _interactiveState;
    private readonly IDeletedUserBarrierRepository? _deletionBarriers;
    private readonly IUserLoginIdentityProjectionService _loginIdentities;

    public UserLookupService(
        IUserRepository users,
        IInteractiveUserDataStateAccessor interactiveState,
        IUserLoginIdentityProjectionService loginIdentities,
        IDeletedUserBarrierRepository? deletionBarriers = null)
    {
        _users = users;
        _interactiveState = interactiveState;
        _loginIdentities = loginIdentities;
        _deletionBarriers = deletionBarriers;
    }

    public async Task<User?> GetUserByUidAsync(Guid uid, CancellationToken ct = default)
    {
        if (_deletionBarriers is not null && await _deletionBarriers.ExistsAsync(uid, ct))
            return null;
        return await _users.GetByIdAsync(uid, ct);
    }

    public async Task<User> GetAndVerifyUserByUidAsync(Guid uid, CancellationToken ct = default)
    {
        var user = await GetUserByUidAsync(uid, ct);
        if (user is null)
            throw new UserNotFoundException();

        user.VerifyIntegrity();
        return user;
    }

    public Task<User> GetAndVerifyUserAsync(Guid token, CancellationToken ct = default) =>
        GetAndVerifyUserByUidAsync(_interactiveState.GetUserIdFromToken(token), ct);

    public Task<UserLoginIdentityMatchResult> ResolveUsernameAsync(byte[] username, CancellationToken ct = default) =>
        _loginIdentities.FindByUsernameAsync(username, ct);

    public async Task<User?> GetUserByUsernameAsync(byte[] username, CancellationToken ct = default)
    {
        var resolution = await ResolveUsernameAsync(username, ct);
        if (resolution.State != UserLoginIdentityMatchState.Matched || !resolution.UserId.HasValue)
            return null;
        return await GetUserByUidAsync(resolution.UserId.Value, ct);
    }

    public async Task<User> GetAndVerifyUserByUsernameAsync(byte[] username, CancellationToken ct = default)
    {
        var user = await GetUserByUsernameAsync(username, ct);
        if (user is null)
            throw new UserNotFoundException();

        user.VerifyIntegrity();
        return user;
    }

    public async Task<IReadOnlyList<User>> GetAndVerifyRememberMeEnabledUsersAsync(CancellationToken ct = default)
    {
        var users = await _users.GetAllRememberMeEnabledUsersAsync(ct);
        var verifiedUsers = new List<User>(users.Count);

        foreach (var user in users)
        {
            if (_deletionBarriers is not null && await _deletionBarriers.ExistsAsync(user.UId, ct))
                continue;
            user.VerifyIntegrity();
            verifiedUsers.Add(user);
        }

        return verifiedUsers;
    }

    public async Task<bool> UserExistsAsync(Guid uid, CancellationToken ct = default)
    {
        var user = await GetUserByUidAsync(uid, ct);
        return user is not null;
    }
}
