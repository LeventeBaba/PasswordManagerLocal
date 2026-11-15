using PasswordManagerLocal.Common.Backend.Models;

namespace PasswordManagerLocal.Common.Backend.Abstractions.Services;

public interface ISyncRuntimeEligibilityService
{
    Task<SyncRuntimeEligibilityResult> EvaluateAsync(CancellationToken ct = default);
}
