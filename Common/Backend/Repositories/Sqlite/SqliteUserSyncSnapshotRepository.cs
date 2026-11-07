using Microsoft.Data.Sqlite;
using PasswordManagerLocal.Common.Backend.Abstractions.Repositories;
using PasswordManagerLocal.Common.Backend.Models;
using PasswordManagerLocal.Common.Backend.Persistence;

namespace PasswordManagerLocal.Common.Backend.Repositories.Sqlite;

internal sealed class SqliteUserSyncSnapshotRepository(SqliteSession session) : IUserSyncSnapshotRepository
{
    private const string Select = """
        SELECT "Id", "ConflictingSnapshotHash", "CreatedAtUtc", "EnvelopePayload", "LastReceivedFromDeviceId",
               "MembershipEpoch", "OriginDeviceId", "OriginInstanceId", "OriginRevision", "OriginSignPublicKey",
               "OriginSignature", "QuarantineReason", "ReceivedAtUtc", "SnapshotHash", "Status", "UserId", "UserKeyEpoch"
        FROM "UserSyncSnapshots"
        """;
    private const string OriginKey = """
        "UserId" = $user AND "OriginDeviceId" = $device AND "OriginInstanceId" = $instance AND "UserKeyEpoch" = $epoch
        """;
    private const string PendingStatus = """ "Status" IN ($pending, $candidate) """;

    private readonly SqliteRowWriter<UserSyncSnapshot> _rows = new(session, "UserSyncSnapshots", 1,
    [
        (nameof(UserSyncSnapshot.Id), row => row.Id),
        (nameof(UserSyncSnapshot.ConflictingSnapshotHash), row => row.ConflictingSnapshotHash),
        (nameof(UserSyncSnapshot.CreatedAtUtc), row => row.CreatedAtUtc),
        (nameof(UserSyncSnapshot.EnvelopePayload), row => row.EnvelopePayload),
        (nameof(UserSyncSnapshot.LastReceivedFromDeviceId), row => row.LastReceivedFromDeviceId),
        (nameof(UserSyncSnapshot.MembershipEpoch), row => row.MembershipEpoch),
        (nameof(UserSyncSnapshot.OriginDeviceId), row => row.OriginDeviceId),
        (nameof(UserSyncSnapshot.OriginInstanceId), row => row.OriginInstanceId),
        (nameof(UserSyncSnapshot.OriginRevision), row => row.OriginRevision),
        (nameof(UserSyncSnapshot.OriginSignPublicKey), row => row.OriginSignPublicKey),
        (nameof(UserSyncSnapshot.OriginSignature), row => row.OriginSignature),
        (nameof(UserSyncSnapshot.QuarantineReason), row => row.QuarantineReason),
        (nameof(UserSyncSnapshot.ReceivedAtUtc), row => row.ReceivedAtUtc),
        (nameof(UserSyncSnapshot.SnapshotHash), row => row.SnapshotHash),
        (nameof(UserSyncSnapshot.Status), row => (byte)row.Status),
        (nameof(UserSyncSnapshot.UserId), row => row.UserId),
        (nameof(UserSyncSnapshot.UserKeyEpoch), row => row.UserKeyEpoch)
    ], concurrencyColumns: [nameof(UserSyncSnapshot.OriginRevision), nameof(UserSyncSnapshot.SnapshotHash)],
        trackChanges: false);

    public Task<UserSyncSnapshot?> GetAsync(Guid userId, Guid originDeviceId, Guid originInstanceId, long userKeyEpoch,
        CancellationToken ct = default) =>
        GetOriginAsync(userId, originDeviceId, originInstanceId, userKeyEpoch, OriginKey, ct);

    public Task<UserSyncSnapshot?> GetExactAsync(Guid userId, Guid originDeviceId, Guid originInstanceId, long userKeyEpoch,
        long originRevision, CancellationToken ct = default) =>
        GetOriginAsync(userId, originDeviceId, originInstanceId, userKeyEpoch,
            $"""{OriginKey} AND "OriginRevision" = $revision""", ct, ("$revision", originRevision));

    public Task<UserSyncSnapshot?> GetLatestLocalAsync(Guid userId, Guid originDeviceId, Guid originInstanceId, long userKeyEpoch,
        CancellationToken ct = default) =>
        GetOriginAsync(userId, originDeviceId, originInstanceId, userKeyEpoch,
            $"""{OriginKey} AND "Status" = $published""", ct, ("$published", (byte)UserSyncSnapshotStatus.LocalPublished));

    public async Task<IReadOnlyList<UserSyncSnapshot>> ListPendingAsync(Guid userId, long userKeyEpoch, long membershipEpoch,
        CancellationToken ct = default) =>
        await session.ReadAsync($"""
            {Select} WHERE "UserId" = $user AND "UserKeyEpoch" = $epoch AND "MembershipEpoch" = $membership AND {PendingStatus}
            ORDER BY "OriginDeviceId", "OriginInstanceId", "OriginRevision";
            """, ReadForWrite, ct, ("$user", userId), ("$epoch", userKeyEpoch), ("$membership", membershipEpoch),
            ("$pending", (byte)UserSyncSnapshotStatus.Pending), ("$candidate", (byte)UserSyncSnapshotStatus.RecoveryCandidate));

    public async Task<IReadOnlyList<UserSyncSnapshot>> ListPendingForKeyEpochAsync(Guid userId, long userKeyEpoch,
        CancellationToken ct = default) =>
        await session.ReadAsync($"""
            {Select} WHERE "UserId" = $user AND "UserKeyEpoch" = $epoch AND {PendingStatus}
            ORDER BY "MembershipEpoch", "OriginDeviceId", "OriginInstanceId", "OriginRevision";
            """, ReadForWrite, ct, ("$user", userId), ("$epoch", userKeyEpoch),
            ("$pending", (byte)UserSyncSnapshotStatus.Pending), ("$candidate", (byte)UserSyncSnapshotStatus.RecoveryCandidate));

    public async Task<IReadOnlyList<UserSyncSnapshot>> ListForUserAsync(Guid userId, CancellationToken ct = default) =>
        await session.ReadAsync($"""
            {Select} WHERE "UserId" = $user
            ORDER BY "UserKeyEpoch", "OriginDeviceId", "OriginInstanceId", "OriginRevision";
            """, ReadForWrite, ct, ("$user", userId));

    public async Task<IReadOnlyList<UserSyncSnapshot>> ListRecoveryEvidenceAsync(Guid userId, long userKeyEpoch,
        CancellationToken ct = default)
    {
        var rows = await session.ReadAsync($"""
            {Select} WHERE "UserId" = $user AND "UserKeyEpoch" = $epoch
              AND "Status" IN ($pending, $candidate, $merged, $published);
            """, ReadForWrite, ct, ("$user", userId), ("$epoch", userKeyEpoch),
            ("$pending", (byte)UserSyncSnapshotStatus.Pending), ("$candidate", (byte)UserSyncSnapshotStatus.RecoveryCandidate),
            ("$merged", (byte)UserSyncSnapshotStatus.MergedReceipt), ("$published", (byte)UserSyncSnapshotStatus.LocalPublished));
        return rows.OrderBy(row => row.OriginDeviceId).ThenBy(row => row.OriginInstanceId)
            .ThenBy(row => row.OriginRevision).ThenBy(row => Convert.ToHexString(row.SnapshotHash), StringComparer.Ordinal).ToArray();
    }

    public Task<bool> HasIsolatedForkAsync(Guid userId, long userKeyEpoch, long membershipEpoch, CancellationToken ct = default) =>
        session.ExistsAsync("""
            SELECT 1 FROM "UserSyncSnapshots" WHERE "UserId" = $user AND "UserKeyEpoch" = $epoch
              AND "MembershipEpoch" = $membership AND "Status" = $fork
            """, ct, ("$user", userId), ("$epoch", userKeyEpoch), ("$membership", membershipEpoch),
            ("$fork", (byte)UserSyncSnapshotStatus.IsolatedFork));

    public Task AddAsync(UserSyncSnapshot snapshot, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        _rows.Stage(snapshot, SqliteMutation.Operation.Insert);
        return Task.CompletedTask;
    }

    public void Update(UserSyncSnapshot snapshot) => _rows.Stage(snapshot, SqliteMutation.Operation.Update);

    public void Delete(UserSyncSnapshot snapshot) => _rows.Stage(snapshot, SqliteMutation.Operation.Delete);

    public void DeleteRange(IEnumerable<UserSyncSnapshot> snapshots)
    {
        foreach (var row in snapshots)
            Delete(row);
    }

    private async Task<UserSyncSnapshot?> GetOriginAsync(Guid userId, Guid deviceId, Guid instanceId, long epoch,
        string predicate, CancellationToken ct, params (string Name, object? Value)[] extra) =>
        (await session.ReadAsync($"{Select} WHERE {predicate};", ReadForWrite, ct,
            [("$user", userId), ("$device", deviceId), ("$instance", instanceId), ("$epoch", epoch), .. extra])).SingleOrDefault();

    private UserSyncSnapshot ReadForWrite(SqliteDataReader row) => _rows.Remember(new UserSyncSnapshot
    {
        Id = row.GetGuid(0),
        ConflictingSnapshotHash = row.NullableBlob(1),
        CreatedAtUtc = row.UtcOffset(2),
        EnvelopePayload = row.Blob(3),
        LastReceivedFromDeviceId = row.NullableGuid(4),
        MembershipEpoch = row.GetInt64(5),
        OriginDeviceId = row.GetGuid(6),
        OriginInstanceId = row.GetGuid(7),
        OriginRevision = row.GetInt64(8),
        OriginSignPublicKey = row.Blob(9),
        OriginSignature = row.Blob(10),
        QuarantineReason = row.NullableText(11),
        ReceivedAtUtc = row.UtcOffset(12),
        SnapshotHash = row.Blob(13),
        Status = (UserSyncSnapshotStatus)row.GetByte(14),
        UserId = row.GetGuid(15),
        UserKeyEpoch = row.GetInt64(16)
    });
}
