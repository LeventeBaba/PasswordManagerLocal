using PasswordManagerLocal.Common.Contracts.Requests;

namespace PasswordManagerLocal.Common.Backend.Abstractions.Services;

public interface IUserLoginService
{
    Task<Guid> LoginAsync(LoginRequest request, CancellationToken ct = default);
}
