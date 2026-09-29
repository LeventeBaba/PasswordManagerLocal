using PasswordManagerLocal.Common.Contracts.Responses;

namespace PasswordManagerLocal.Common.Backend.Abstractions.Services;

public interface ISelfDiagnosticsService
{
    Task<SelfDiagnosticsResultResponse> RunAsync(Guid token, CancellationToken ct = default);
}
