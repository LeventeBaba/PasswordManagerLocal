using PasswordManagerLocal.Common.Contracts.Requests;

namespace PasswordManagerLocal.Common.Backend.Abstractions.Services;

public interface IUserCustomColorService
{
    Task AddCustomUserColorsAsync(Guid token, IReadOnlyList<NewCustomUserColorRequest> requests, CancellationToken ct = default);
    Task DeleteCustomUserColorsAsync(Guid token, IReadOnlyList<Guid> customUserColorIds, CancellationToken ct = default);
    Task ExportCustomUserColorsToUserAsync(Guid sourceToken, ExportCustomUserColorsToUserRequest request, CancellationToken ct = default);
    Task UpdateCustomUserColorAsync(Guid token, UpdateCustomUserColorRequest request, CancellationToken ct = default);
}
