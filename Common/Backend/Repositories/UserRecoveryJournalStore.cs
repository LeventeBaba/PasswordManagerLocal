using Microsoft.Data.Sqlite;
using PasswordManagerLocal.Common.Backend.Abstractions.Persistence;
using PasswordManagerLocal.Common.Backend.Models;
using PasswordManagerLocal.Common.Backend.Persistence;

namespace PasswordManagerLocal.Common.Backend.Repositories;

internal sealed class UserRecoveryJournalStore(SqliteSession session) : IUserRecoveryJournalStore
{
    private const string SelectById = """
        SELECT "Id", "UserId", "State", "CreatedAtUtc", "ProtectedKey", "EncryptedPayload", "PayloadHash"
        FROM "UserRecoveryJournals" WHERE "Id" = $id;
        """;

    private readonly SqliteRowWriter<UserRecoveryJournal> _rows = new(session, "UserRecoveryJournals", 1,
    [
        (nameof(UserRecoveryJournal.Id), row => row.Id),
        (nameof(UserRecoveryJournal.UserId), row => row.UserId),
        (nameof(UserRecoveryJournal.State), row => (byte)row.State),
        (nameof(UserRecoveryJournal.CreatedAtUtc), row => row.CreatedAtUtc),
        (nameof(UserRecoveryJournal.ProtectedKey), row => row.ProtectedKey),
        (nameof(UserRecoveryJournal.EncryptedPayload), row => row.EncryptedPayload),
        (nameof(UserRecoveryJournal.PayloadHash), row => row.PayloadHash)
    ], [nameof(UserRecoveryJournal.EncryptedPayload)], trackChanges: false);

    public bool HasActiveTransaction => session.HasActiveTransaction;

    public void Add(UserRecoveryJournal row) => _rows.Stage(row, SqliteMutation.Operation.Insert);
    public void Update(UserRecoveryJournal row) => _rows.Stage(row, SqliteMutation.Operation.Update);
    public void Delete(UserRecoveryJournal row) => _rows.Stage(row, SqliteMutation.Operation.Delete);

    public async Task<UserRecoveryJournal?> GetForWriteAsync(Guid journalId, CancellationToken ct) =>
        (await session.ReadAsync(SelectById, row => _rows.Remember(Read(row)), ct, ("$id", journalId))).SingleOrDefault();

    public async Task<UserRecoveryJournal?> ReadPersistedAsync(Guid journalId, CancellationToken ct) =>
        (await session.ReadAsync(SelectById, Read, ct, ("$id", journalId))).SingleOrDefault();

    public async Task<Guid?> ReadUserIdAsync(Guid journalId, CancellationToken ct) =>
        (await session.ReadAsync("""SELECT "UserId" FROM "UserRecoveryJournals" WHERE "Id" = $id;""",
            row => (Guid?)row.GetGuid(0), ct, ("$id", journalId))).SingleOrDefault();

    public async Task<IReadOnlyList<Guid>> ListIdsAsync(CancellationToken ct) =>
        await session.ReadAsync("""SELECT "Id" FROM "UserRecoveryJournals";""", row => row.GetGuid(0), ct);

    public Task<bool> HasAppliedControlStateAsync(Guid userId, long keyEpoch, long membershipEpoch, CancellationToken ct) =>
        session.ExistsAsync("""
            SELECT 1 FROM "UserControlStates" WHERE "UserId" = $user AND "HasConflict" = 0
                AND "AppliedKeyEpoch" = $key AND "AppliedMembershipEpoch" = $membership
            """, ct, ("$user", userId), ("$key", keyEpoch), ("$membership", membershipEpoch));

    public async Task<LocalUserDevice?> ReadLocalLinkAsync(Guid userId, CancellationToken ct) =>
        (await session.ReadAsync($"""
            SELECT {LocalUserDeviceRepository.Projection} FROM "LocalUserDevices" lu WHERE lu."UserId" = $user;
            """, LocalUserDeviceRepository.ReadRow, ct, ("$user", userId))).SingleOrDefault();

    public async Task<byte[]?> ReadActiveSigningKeyAsync(Guid userId, Guid deviceId, Guid originInstanceId, CancellationToken ct) =>
        (await session.ReadAsync("""
            SELECT "SignPublicKey" FROM "UserMembershipAuthorizations"
            WHERE "UserId" = $user AND "DeviceId" = $device AND "OriginInstanceId" = $instance AND "IsActive" = 1;
            """, row => row.Blob(0), ct, ("$user", userId), ("$device", deviceId), ("$instance", originInstanceId))).SingleOrDefault();

    public async Task<IReadOnlyList<Guid>> ListActiveDeviceIdsAsync(Guid userId, CancellationToken ct) =>
        await session.ReadAsync("""
            SELECT "DeviceId" FROM "UserMembershipAuthorizations" WHERE "UserId" = $user AND "IsActive" = 1;
            """, row => row.GetGuid(0), ct, ("$user", userId));

    public Task<bool> HasBlockingFaultsAsync(Guid userId, CancellationToken ct) =>
        session.ExistsAsync("""
            SELECT 1 FROM "UserSyncFaults" WHERE "UserId" = $user AND "Status" NOT IN ($recovered, $superseded)
                AND ("BlocksPublishing" = 1 OR "BlocksLogin" = 1 OR "BlocksMerge" = 1 OR "BlocksLifecycle" = 1)
            """, ct, ("$user", userId), ("$recovered", (byte)UserSyncHealthStatus.Recovered),
            ("$superseded", (byte)UserSyncHealthStatus.Superseded));

    public async Task<UserRevisionKnowledge?> ReadKnowledgeAsync(Guid userId, Guid deviceId, Guid originInstanceId,
        long keyEpoch, CancellationToken ct) =>
        (await session.ReadAsync($"""
            {UserRevisionKnowledgeRepository.Select}
            WHERE "UserId" = $user AND "OriginDeviceId" = $device AND "OriginInstanceId" = $instance AND "UserKeyEpoch" = $epoch;
            """, UserRevisionKnowledgeRepository.Read, ct, ("$user", userId), ("$device", deviceId),
            ("$instance", originInstanceId), ("$epoch", keyEpoch))).SingleOrDefault();

    public async Task<byte[]?> ReadRejectedOperationHashAsync(Guid userId, Guid operationId, CancellationToken ct) =>
        (await session.ReadAsync("""
            SELECT "OperationHash" FROM "UserControlOperations"
            WHERE "UserId" = $user AND "OperationId" = $operation AND "Status" = $rejected;
            """, row => row.Blob(0), ct, ("$user", userId), ("$operation", operationId),
            ("$rejected", (byte)UserControlOperationStatus.Rejected))).SingleOrDefault();

    private static UserRecoveryJournal Read(SqliteDataReader row) => new()
    {
        Id = row.GetGuid(0),
        UserId = row.GetGuid(1),
        State = (UserRecoveryJournalState)row.GetInt32(2),
        CreatedAtUtc = row.UtcOffset(3),
        ProtectedKey = row.Blob(4),
        EncryptedPayload = row.Blob(5),
        PayloadHash = row.Blob(6)
    };
}
