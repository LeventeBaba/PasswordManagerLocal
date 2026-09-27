using Microsoft.EntityFrameworkCore;
using PasswordManagerLocal.Common.Backend.Abstractions.Repositories;
using PasswordManagerLocal.Common.Backend.Models;
using PasswordManagerLocal.Common.Backend.Persistence;

namespace PasswordManagerLocal.Common.Backend.Repositories;

public sealed class UserSyncFaultRepository : IUserSyncFaultRepository
{
    private readonly DbSet<UserSyncFault> _faults;

    public UserSyncFaultRepository(AppDbContext context) => _faults = context.UserSyncFaults;

    public async Task<UserSyncFault?> GetActiveAsync(
        Guid userId,
        UserSyncFaultScope scope,
        UserSyncFaultKind kind,
        Guid? originDeviceId,
        Guid? originInstanceId,
        long? keyEpoch,
        CancellationToken ct = default)
    {
        // SQLite stores DateTimeOffset values but cannot translate ORDER BY for that CLR type.
        // Keep the selective predicate in SQL, then order the small scoped result set in memory.
        var matches = await _faults
            .Where(fault =>
                fault.UserId == userId &&
                fault.Scope == scope &&
                fault.Kind == kind &&
                fault.OriginDeviceId == originDeviceId &&
                fault.OriginInstanceId == originInstanceId &&
                fault.KeyEpoch == keyEpoch &&
                fault.Status != UserSyncHealthStatus.Recovered &&
                fault.Status != UserSyncHealthStatus.Superseded)
            .ToListAsync(ct);

        return matches
            .OrderByDescending(fault => fault.LastDetectedAtUtc)
            .ThenByDescending(fault => fault.Id)
            .FirstOrDefault();
    }

    public async Task<IReadOnlyList<UserSyncFault>> ListForUserAsync(
        Guid userId,
        CancellationToken ct = default)
    {
        var rows = await _faults
            .Where(fault => fault.UserId == userId)
            .ToListAsync(ct);

        return rows
            .OrderBy(fault => fault.FirstDetectedAtUtc)
            .ThenBy(fault => fault.Id)
            .ToArray();
    }

    public async Task<IReadOnlyList<UserSyncFault>> ListActiveForUserAsync(
        Guid userId,
        CancellationToken ct = default)
    {
        var rows = await _faults
            .Where(fault =>
                fault.UserId == userId &&
                fault.Status != UserSyncHealthStatus.Recovered &&
                fault.Status != UserSyncHealthStatus.Superseded)
            .ToListAsync(ct);

        return rows
            .OrderBy(fault => fault.FirstDetectedAtUtc)
            .ThenBy(fault => fault.Id)
            .ToArray();
    }

    public async Task<IReadOnlyList<UserSyncFault>> ListRecoverableLocalCanonicalAsync(
        DateTimeOffset nowUtc,
        CancellationToken ct = default)
    {
        var rows = await _faults
            .Where(fault =>
                fault.Scope == UserSyncFaultScope.LocalCanonical &&
                fault.Status != UserSyncHealthStatus.Recovered &&
                fault.Status != UserSyncHealthStatus.Superseded &&
                fault.Status != UserSyncHealthStatus.TerminalConflict)
            .ToListAsync(ct);

        return rows
            .Where(fault => !fault.NextRecoveryAttemptAtUtc.HasValue || fault.NextRecoveryAttemptAtUtc <= nowUtc)
            .OrderBy(fault => fault.NextRecoveryAttemptAtUtc ?? DateTimeOffset.MinValue)
            .ThenBy(fault => fault.UserId)
            .ToArray();
    }

    public Task<bool> HasBlockingPublishingFaultAsync(Guid userId, CancellationToken ct = default) =>
        _faults.AnyAsync(fault =>
            fault.UserId == userId &&
            fault.BlocksPublishing &&
            fault.Status != UserSyncHealthStatus.Recovered &&
            fault.Status != UserSyncHealthStatus.Superseded, ct);

    public Task<bool> HasTerminalOriginFaultAsync(
        Guid userId,
        Guid originDeviceId,
        Guid originInstanceId,
        long keyEpoch,
        CancellationToken ct = default) =>
        _faults.AnyAsync(fault =>
            fault.UserId == userId &&
            fault.OriginDeviceId == originDeviceId &&
            fault.OriginInstanceId == originInstanceId &&
            fault.KeyEpoch == keyEpoch &&
            fault.Status == UserSyncHealthStatus.TerminalConflict, ct);

    public Task AddAsync(UserSyncFault fault, CancellationToken ct = default) =>
        _faults.AddAsync(fault, ct).AsTask();

    public void Update(UserSyncFault fault) => _faults.Update(fault);
    public void DeleteRange(IEnumerable<UserSyncFault> faults) => _faults.RemoveRange(faults);
}
