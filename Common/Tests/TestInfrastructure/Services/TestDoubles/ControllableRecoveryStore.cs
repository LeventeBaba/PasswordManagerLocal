using PasswordManagerLocal.Common.Backend.Abstractions.Persistence;
using PasswordManagerLocal.Common.Backend.Internal.Recovery;

namespace PasswordManagerLocal.Common.Tests.TestInfrastructure.Services.TestDoubles;

internal sealed class ControllableRecoveryStore(IUserCanonicalRecoveryStore inner) : IUserCanonicalRecoveryStore
{
    public Func<CancellationToken, Task>? AfterCaptureAsync { get; set; }
    public Func<CancellationToken, Task>? BeforeRevalidationAsync { get; set; }

    public async Task<UserRecoveryInput> CaptureAsync(Guid userId, CancellationToken ct = default)
    {
        var input = await inner.CaptureAsync(userId, ct);
        try
        {
            if (AfterCaptureAsync is { } callback) await callback(ct);
            return input;
        }
        catch { input.Dispose(); throw; }
    }

    public async Task<bool> IsCurrentAsync(UserRecoveryInput input, CancellationToken ct = default)
    {
        if (BeforeRevalidationAsync is { } callback) await callback(ct);
        return await inner.IsCurrentAsync(input, ct);
    }
}
