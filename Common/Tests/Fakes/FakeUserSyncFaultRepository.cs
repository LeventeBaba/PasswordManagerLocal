using PasswordManagerLocal.Common.Backend.Abstractions.Repositories;
using PasswordManagerLocal.Common.Backend.Models;
using PasswordManagerLocal.Common.Backend.Sync;

namespace PasswordManagerLocal.Common.Tests.Fakes;

public sealed class FakeUserSyncFaultRepository : IUserSyncFaultRepository
{
    private readonly List<UserSyncFault> _rows = [];
    private static bool Active(UserSyncFault row) => row.Status is not
        (UserSyncHealthStatus.Recovered or UserSyncHealthStatus.Superseded or UserSyncHealthStatus.RetiredOrigin);
    public Task<UserSyncFault?> GetActiveAsync(Guid userId, UserSyncFaultScope scope, UserSyncFaultKind kind,
        Guid? originDeviceId, Guid? originInstanceId, long? keyEpoch, CancellationToken ct = default) =>
        Task.FromResult(_rows.LastOrDefault(row => Active(row) && row.UserId == userId && row.Scope == scope &&
            row.Kind == kind && row.OriginDeviceId == originDeviceId && row.OriginInstanceId == originInstanceId && row.KeyEpoch == keyEpoch));
    public Task<IReadOnlyList<UserSyncFault>> ListForUserAsync(Guid userId, CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyList<UserSyncFault>>(_rows.Where(row => row.UserId == userId).ToArray());
    public Task<IReadOnlyList<UserSyncFault>> ListActiveForUserAsync(Guid userId, CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyList<UserSyncFault>>(_rows.Where(row => row.UserId == userId && Active(row)).ToArray());
    public Task<IReadOnlyList<UserSyncFault>> ListRecoverableLocalCanonicalAsync(DateTimeOffset nowUtc, CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyList<UserSyncFault>>(_rows.Where(row => Active(row) &&
            UserSyncFaultPolicy.IsRecoverableCanonical(row) &&
            !_rows.Any(blocker => blocker.UserId == row.UserId && Active(blocker) && UserSyncFaultPolicy.IsTerminal(blocker)) &&
            (!row.NextRecoveryAttemptAtUtc.HasValue || row.NextRecoveryAttemptAtUtc <= nowUtc)).ToArray());
    public Task<bool> HasBlockingPublishingFaultAsync(Guid userId, CancellationToken ct = default) =>
        Task.FromResult(_rows.Any(row => row.UserId == userId && Active(row) && row.BlocksPublishing));
    public Task<bool> HasTerminalOriginFaultAsync(Guid userId, Guid originDeviceId, Guid originInstanceId, long keyEpoch, CancellationToken ct = default) =>
        Task.FromResult(_rows.Any(row => row.UserId == userId && row.OriginDeviceId == originDeviceId &&
            row.OriginInstanceId == originInstanceId && row.KeyEpoch == keyEpoch && row.Status == UserSyncHealthStatus.TerminalConflict));
    public Task AddAsync(UserSyncFault fault, CancellationToken ct = default) { _rows.Add(fault); return Task.CompletedTask; }
    public void Update(UserSyncFault fault) { }
    public void DeleteRange(IEnumerable<UserSyncFault> faults) { foreach (var fault in faults.ToArray()) _rows.Remove(fault); }
}
