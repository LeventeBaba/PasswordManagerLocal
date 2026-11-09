using PasswordManagerLocal.Common.Backend.Models;
using PasswordManagerLocal.Common.Backend.Models.Encrypted;

namespace PasswordManagerLocal.Common.Backend.Abstractions.Services;

public interface IUserLoginIdentityProjectionService
{
    Task<UserLoginIdentityState> SetCanonicalAsync(
        User user,
        SyncVersionStamp generalUserDataVersion,
        CancellationToken ct = default);

    Task<UserLoginIdentityState?> RecalculateAsync(Guid userId, CancellationToken ct = default);
    Task<UserLoginIdentityState?> RecalculateUnderLifecycleAsync(Guid userId, CancellationToken ct = default);

    // A signed, uniquely identified candidate is only a hint. Login must prove the password,
    // repair from authenticated evidence and revalidate the active projection before issuing a token.
    Task<UserLoginIdentityMatchResult> FindSignedRecoveryCandidateAsync(byte[] normalizedUsername, CancellationToken ct = default) =>
        Task.FromResult(new UserLoginIdentityMatchResult(UserLoginIdentityMatchState.NotFound));

    Task<UserLoginIdentityMatchResult> FindByUsernameAsync(
        byte[] normalizedUsername,
        CancellationToken ct = default);
}
