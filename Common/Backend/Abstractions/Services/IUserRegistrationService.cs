using PasswordManagerLocal.Common.Contracts.Requests;

namespace PasswordManagerLocal.Common.Backend.Abstractions.Services;

public interface IUserRegistrationService
{
    Task<Guid> RegisterAsync(RegistrationRequest request, CancellationToken ct = default);
}
