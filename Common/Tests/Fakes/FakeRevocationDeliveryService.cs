using PasswordManagerLocal.Common.Backend.Abstractions.Services;
using PasswordManagerLocal.Common.Backend.Models;

namespace PasswordManagerLocal.Common.Tests.Fakes;

public sealed class FakeRevocationDeliveryService : IRevocationDeliveryService
{
    public bool Pending { get; set; }
    public Task<bool> HasPendingDeliveryAsync(CancellationToken ct = default) => Task.FromResult(Pending);
    public Task<bool> HasPendingForPeerAsync(Guid deviceId, CancellationToken ct = default) => Task.FromResult(Pending);
    public Task<IReadOnlyList<Guid>> ListPendingUserIdsAsync(CancellationToken ct = default) => Task.FromResult<IReadOnlyList<Guid>>([]);
    public Task<IReadOnlyList<RevocationDeliverySnapshot>> GetSnapshotsAsync(CancellationToken ct = default) => Task.FromResult<IReadOnlyList<RevocationDeliverySnapshot>>([]);
    public Task StageHistoryResetAsync(Guid userId, CancellationToken ct = default) => Task.CompletedTask;
}
