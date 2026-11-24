using PasswordManagerLocal.Common.Contracts.Requests;

namespace PasswordManagerLocal.Common.Backend.Abstractions.Services;

public interface IUserLocalProfileService
{
    Task RemoveAsync(LoginRequest request, bool acknowledgeDataLoss, CancellationToken ct = default);
}
