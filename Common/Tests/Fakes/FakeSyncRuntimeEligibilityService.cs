using PasswordManagerLocal.Common.Backend.Abstractions.Services;
using PasswordManagerLocal.Common.Backend.Models;

namespace PasswordManagerLocal.Common.Tests.Fakes;

public sealed class FakeSyncRuntimeEligibilityService : ISyncRuntimeEligibilityService
{
    public SyncRuntimeEligibilityResult Result { get; set; } = new(SyncRuntimeEligibilityReason.NoLocalProfiles);
    public Func<CancellationToken, Task<SyncRuntimeEligibilityResult>>? Handler { get; set; }
    public int Calls { get; private set; }
    public Task<SyncRuntimeEligibilityResult> EvaluateAsync(CancellationToken ct = default)
    {
        Calls++;
        return Handler?.Invoke(ct) ?? Task.FromResult(Result);
    }
}
