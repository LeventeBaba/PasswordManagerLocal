using PasswordManagerLocal.Common.Contracts.Requests;
using PasswordManagerLocal.Common.Contracts.Responses;

namespace PasswordManagerLocal.Common.Backend.Abstractions.Services;

public interface IUserProfileService
{
    Task<UserProfileInfoResponse> GetUserProfileInfoAsync(Guid token, CancellationToken ct = default);
    Task DeleteUserAccountAsync(Guid token, byte[] password, CancellationToken ct = default);
    Task ChangeUsernameAsync(Guid token, string newUsername, CancellationToken ct = default);
    Task UpdateUserProfileInfoAsync(UpdateUserProfileRequest request, CancellationToken ct = default);
}