using PasswordManagerLocal.Common.Backend.Models;

namespace PasswordManagerLocal.Common.Backend.Abstractions.Services;

public interface IDatabaseHealthService
{
    Task<DatabaseHealthCheckResult> CheckAsync(CancellationToken ct = default);
}
