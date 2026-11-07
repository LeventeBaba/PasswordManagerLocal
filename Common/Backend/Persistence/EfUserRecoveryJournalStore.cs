using Microsoft.EntityFrameworkCore;
using PasswordManagerLocal.Common.Backend.Abstractions.Persistence;
using PasswordManagerLocal.Common.Backend.Models;

namespace PasswordManagerLocal.Common.Backend.Persistence;

internal sealed class EfUserRecoveryJournalStore(AppDbContext db) : IUserRecoveryJournalStore
{
    public bool HasActiveTransaction => db.Database.CurrentTransaction is not null;

    public void Add(UserRecoveryJournal row) => db.UserRecoveryJournals.Add(row);
    public void Update(UserRecoveryJournal row) => db.UserRecoveryJournals.Update(row);
    public void Delete(UserRecoveryJournal row) => db.UserRecoveryJournals.Remove(row);

    public Task<UserRecoveryJournal?> GetForWriteAsync(Guid journalId, CancellationToken ct) =>
        db.UserRecoveryJournals.SingleOrDefaultAsync(row => row.Id == journalId, ct);

    public Task<UserRecoveryJournal?> ReadPersistedAsync(Guid journalId, CancellationToken ct) =>
        db.UserRecoveryJournals.AsNoTracking().SingleOrDefaultAsync(row => row.Id == journalId, ct);

    public Task<Guid?> ReadUserIdAsync(Guid journalId, CancellationToken ct) =>
        db.UserRecoveryJournals.AsNoTracking().Where(row => row.Id == journalId)
            .Select(row => (Guid?)row.UserId).SingleOrDefaultAsync(ct);

    public async Task<IReadOnlyList<Guid>> ListIdsAsync(CancellationToken ct) =>
        await db.UserRecoveryJournals.AsNoTracking().Select(row => row.Id).ToListAsync(ct);

    public Task<bool> HasAppliedControlStateAsync(Guid userId, long keyEpoch, long membershipEpoch, CancellationToken ct) =>
        db.UserControlStates.AsNoTracking().AnyAsync(row => row.UserId == userId && !row.HasConflict &&
            row.AppliedKeyEpoch == keyEpoch && row.AppliedMembershipEpoch == membershipEpoch, ct);

    public Task<LocalUserDevice?> ReadLocalLinkAsync(Guid userId, CancellationToken ct) =>
        db.LocalUserDevices.AsNoTracking().SingleOrDefaultAsync(row => row.UserId == userId, ct);

    public Task<byte[]?> ReadActiveSigningKeyAsync(Guid userId, Guid deviceId, Guid originInstanceId, CancellationToken ct) =>
        db.UserMembershipAuthorizations.AsNoTracking().Where(row => row.UserId == userId && row.IsActive &&
            row.DeviceId == deviceId && row.OriginInstanceId == originInstanceId)
            .Select(row => row.SignPublicKey).SingleOrDefaultAsync(ct);

    public async Task<IReadOnlyList<Guid>> ListActiveDeviceIdsAsync(Guid userId, CancellationToken ct) =>
        await db.UserMembershipAuthorizations.AsNoTracking().Where(row => row.UserId == userId && row.IsActive)
            .Select(row => row.DeviceId).ToListAsync(ct);

    public Task<bool> HasBlockingFaultsAsync(Guid userId, CancellationToken ct) =>
        db.UserSyncFaults.AnyAsync(row => row.UserId == userId && row.Status != UserSyncHealthStatus.Recovered &&
            row.Status != UserSyncHealthStatus.Superseded &&
            (row.BlocksPublishing || row.BlocksLogin || row.BlocksMerge || row.BlocksLifecycle), ct);

    public Task<UserRevisionKnowledge?> ReadKnowledgeAsync(Guid userId, Guid deviceId, Guid originInstanceId,
        long keyEpoch, CancellationToken ct) =>
        db.UserRevisionKnowledge.AsNoTracking().SingleOrDefaultAsync(row => row.UserId == userId &&
            row.OriginDeviceId == deviceId && row.OriginInstanceId == originInstanceId && row.UserKeyEpoch == keyEpoch, ct);

    public Task<byte[]?> ReadRejectedOperationHashAsync(Guid userId, Guid operationId, CancellationToken ct) =>
        db.UserControlOperations.AsNoTracking().Where(row => row.UserId == userId && row.OperationId == operationId &&
            row.Status == UserControlOperationStatus.Rejected).Select(row => row.OperationHash).SingleOrDefaultAsync(ct);
}
