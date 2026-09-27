using PasswordManagerLocal.Common.Contracts.Requests;

namespace PasswordManagerLocal.Common.Backend.Abstractions.Services;

public interface IMasterPasswordRotationService
{
    Task ChangeMasterPasswordAsync(MasterPasswordChangeRequest request, CancellationToken ct = default);
}
