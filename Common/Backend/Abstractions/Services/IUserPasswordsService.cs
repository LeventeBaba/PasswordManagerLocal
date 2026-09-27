using PasswordManagerLocal.Common.Contracts.Requests;
using PasswordManagerLocal.Common.Contracts.Responses;

namespace PasswordManagerLocal.Common.Backend.Abstractions.Services;

public interface IUserPasswordsService
{
    Task<SavedPasswordsResponse> GetSavedPasswordsAsync(Guid token, CancellationToken ct = default);
    Task AddNewPasswordAsync(Guid token, NewPasswordRequest request, CancellationToken ct = default);
    Task RemovePasswordsAsync(Guid token, IReadOnlyList<Guid> passwordIds, CancellationToken ct = default);
    Task<byte[]> GetUnsecurePasswordAsync(Guid token, Guid passwordId, CancellationToken ct = default);
    Task UpdatePasswordAsync(Guid token, UpdatePasswordRequest request, CancellationToken ct = default);
    Task ExportPasswordsToUserAsync(Guid sourceToken, ExportPasswordsToUserRequest request, CancellationToken ct = default);
}
