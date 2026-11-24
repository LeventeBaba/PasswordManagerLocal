using Microsoft.Data.Sqlite;
using PasswordManagerLocal.Common.Backend.Abstractions.Repositories;
using PasswordManagerLocal.Common.Backend.Models;
using PasswordManagerLocal.Common.Backend.Persistence;
using PasswordManagerLocal.Common.Backend.Sync;

namespace PasswordManagerLocal.Common.Backend.Repositories;

internal sealed class UserSyncFaultRepository(SqliteSession session) : IUserSyncFaultRepository
{
    private const string Select = """
        SELECT "Id", "AffectedComponent", "BlocksGarbageCollection", "BlocksLifecycle", "BlocksLogin", "BlocksMerge",
               "BlocksPublishing", "ConflictingHash", "DiagnosticCode", "ExpectedHash", "FirstDetectedAtUtc", "KeyEpoch",
               "Kind", "LastDetectedAtUtc", "LastRecoveryAttemptAtUtc", "MembershipEpoch", "NextRecoveryAttemptAtUtc",
               "ObservedHash", "OriginDeviceId", "OriginInstanceId", "OriginRevision", "RecoveredAtUtc",
               "RecoveryAttemptCount", "Scope", "Status", "SupersedingRevision", "UserId"
        FROM "UserSyncFaults"
        """;
    private static readonly string Active = $""" "Status" NOT IN ($recovered, $superseded, {(byte)UserSyncHealthStatus.RetiredOrigin}) """;
    private const string Origin = """ "OriginDeviceId" IS $device AND "OriginInstanceId" IS $instance AND "KeyEpoch" IS $epoch """;

    private readonly SqliteRowWriter<UserSyncFault> _rows = new(session, "UserSyncFaults", 1,
    [
        (nameof(UserSyncFault.Id), row => row.Id),
        (nameof(UserSyncFault.AffectedComponent), row => row.AffectedComponent),
        (nameof(UserSyncFault.BlocksGarbageCollection), row => row.BlocksGarbageCollection),
        (nameof(UserSyncFault.BlocksLifecycle), row => row.BlocksLifecycle),
        (nameof(UserSyncFault.BlocksLogin), row => row.BlocksLogin),
        (nameof(UserSyncFault.BlocksMerge), row => row.BlocksMerge),
        (nameof(UserSyncFault.BlocksPublishing), row => row.BlocksPublishing),
        (nameof(UserSyncFault.ConflictingHash), row => row.ConflictingHash),
        (nameof(UserSyncFault.DiagnosticCode), row => row.DiagnosticCode),
        (nameof(UserSyncFault.ExpectedHash), row => row.ExpectedHash),
        (nameof(UserSyncFault.FirstDetectedAtUtc), row => row.FirstDetectedAtUtc),
        (nameof(UserSyncFault.KeyEpoch), row => row.KeyEpoch),
        (nameof(UserSyncFault.Kind), row => (byte)row.Kind),
        (nameof(UserSyncFault.LastDetectedAtUtc), row => row.LastDetectedAtUtc),
        (nameof(UserSyncFault.LastRecoveryAttemptAtUtc), row => row.LastRecoveryAttemptAtUtc),
        (nameof(UserSyncFault.MembershipEpoch), row => row.MembershipEpoch),
        (nameof(UserSyncFault.NextRecoveryAttemptAtUtc), row => row.NextRecoveryAttemptAtUtc),
        (nameof(UserSyncFault.ObservedHash), row => row.ObservedHash),
        (nameof(UserSyncFault.OriginDeviceId), row => row.OriginDeviceId),
        (nameof(UserSyncFault.OriginInstanceId), row => row.OriginInstanceId),
        (nameof(UserSyncFault.OriginRevision), row => row.OriginRevision),
        (nameof(UserSyncFault.RecoveredAtUtc), row => row.RecoveredAtUtc),
        (nameof(UserSyncFault.RecoveryAttemptCount), row => row.RecoveryAttemptCount),
        (nameof(UserSyncFault.Scope), row => (byte)row.Scope),
        (nameof(UserSyncFault.Status), row => (byte)row.Status),
        (nameof(UserSyncFault.SupersedingRevision), row => row.SupersedingRevision),
        (nameof(UserSyncFault.UserId), row => row.UserId)
    ], trackChanges: false, maintenanceColumns:
    [nameof(UserSyncFault.Status), nameof(UserSyncFault.SupersedingRevision), nameof(UserSyncFault.ObservedHash), nameof(UserSyncFault.ConflictingHash)],
        requestMaintenanceOnChange: true);

    public async Task<UserSyncFault?> GetActiveAsync(Guid userId, UserSyncFaultScope scope, UserSyncFaultKind kind,
        Guid? originDeviceId, Guid? originInstanceId, long? keyEpoch, CancellationToken ct = default)
    {
        var rows = await ReadEffectiveAsync($"""
            "UserId" = $user AND "Scope" = $scope AND "Kind" = $kind AND {Origin} AND {Active}
            """, row => row.UserId == userId && row.Scope == scope && row.Kind == kind &&
                row.OriginDeviceId == originDeviceId && row.OriginInstanceId == originInstanceId && row.KeyEpoch == keyEpoch &&
                IsActive(row), ct,
            ("$user", userId), ("$scope", (byte)scope), ("$kind", (byte)kind), ("$device", originDeviceId),
            ("$instance", originInstanceId), ("$epoch", keyEpoch), ("$recovered", (byte)UserSyncHealthStatus.Recovered),
            ("$superseded", (byte)UserSyncHealthStatus.Superseded));
        return rows.OrderByDescending(row => row.LastDetectedAtUtc).ThenByDescending(row => row.Id).FirstOrDefault();
    }

    public async Task<IReadOnlyList<UserSyncFault>> ListForUserAsync(Guid userId, CancellationToken ct = default) =>
        (await ReadEffectiveAsync(""" "UserId" = $user """, row => row.UserId == userId, ct, ("$user", userId)))
            .OrderBy(row => row.FirstDetectedAtUtc).ThenBy(row => row.Id).ToArray();

    public async Task<IReadOnlyList<UserSyncFault>> ListActiveForUserAsync(Guid userId, CancellationToken ct = default) =>
        (await ReadEffectiveAsync($""" "UserId" = $user AND {Active} """, row => row.UserId == userId && IsActive(row), ct,
            ("$user", userId), ("$recovered", (byte)UserSyncHealthStatus.Recovered),
            ("$superseded", (byte)UserSyncHealthStatus.Superseded)))
            .OrderBy(row => row.FirstDetectedAtUtc).ThenBy(row => row.Id).ToArray();

    public async Task<IReadOnlyList<UserSyncFault>> ListRecoverableLocalCanonicalAsync(DateTimeOffset nowUtc, CancellationToken ct = default)
    {
        var rows = await ReadEffectiveAsync($""" "Scope" = $local AND {Active} AND "Status" <> $terminal """,
            row => row.Scope == UserSyncFaultScope.LocalCanonical && IsActive(row) && row.Status != UserSyncHealthStatus.TerminalConflict,
            ct, ("$local", (byte)UserSyncFaultScope.LocalCanonical), ("$recovered", (byte)UserSyncHealthStatus.Recovered),
            ("$superseded", (byte)UserSyncHealthStatus.Superseded), ("$terminal", (byte)UserSyncHealthStatus.TerminalConflict));
        var due = rows.Where(row => UserSyncFaultPolicy.IsRecoverableCanonical(row) &&
            (row.NextRecoveryAttemptAtUtc is null || row.NextRecoveryAttemptAtUtc <= nowUtc)).ToArray();
        var blockedUsers = new HashSet<Guid>();
        foreach (var userId in due.Select(row => row.UserId).Distinct())
        {
            // Public fault metadata can outlive explicit local profile erasure.
            // Such retained evidence must not schedule futile canonical recovery.
            if (!await session.ExistsAsync("""SELECT 1 FROM "Users" WHERE "UId" = $user""", ct, ("$user", userId)))
            {
                blockedUsers.Add(userId);
                continue;
            }
            if ((await ListActiveForUserAsync(userId, ct)).Any(UserSyncFaultPolicy.IsTerminal) ||
                await new UserSyncSnapshotRepository(session).HasIsolatedForkAsync(userId, ct))
                blockedUsers.Add(userId);
        }
        return due.Where(row => !blockedUsers.Contains(row.UserId))
            .OrderBy(row => row.NextRecoveryAttemptAtUtc ?? DateTimeOffset.MinValue).ThenBy(row => row.UserId).ToArray();
    }

    public async Task<bool> HasBlockingPublishingFaultAsync(Guid userId, CancellationToken ct = default)
    {
        if (session.GetPendingRows<UserSyncFault>().Any(item => item.Row.UserId == userId))
            return (await ListActiveForUserAsync(userId, ct)).Any(row => row.BlocksPublishing);

        return await session.ExistsAsync($"""
            SELECT 1 FROM "UserSyncFaults" WHERE "UserId" = $user AND "BlocksPublishing" = 1 AND {Active}
            """, ct, ("$user", userId), ("$recovered", (byte)UserSyncHealthStatus.Recovered),
            ("$superseded", (byte)UserSyncHealthStatus.Superseded));
    }

    public async Task<bool> HasTerminalOriginFaultAsync(Guid userId, Guid originDeviceId, Guid originInstanceId, long keyEpoch,
        CancellationToken ct = default)
    {
        if (session.GetPendingRows<UserSyncFault>().Any(item => item.Row.UserId == userId))
        {
            return (await ListActiveForUserAsync(userId, ct)).Any(row => row.OriginDeviceId == originDeviceId &&
                row.OriginInstanceId == originInstanceId && row.KeyEpoch == keyEpoch &&
                row.Status == UserSyncHealthStatus.TerminalConflict);
        }

        return await session.ExistsAsync($"""
            SELECT 1 FROM "UserSyncFaults" WHERE "UserId" = $user AND {Origin} AND "Status" = $terminal
            """, ct, ("$user", userId), ("$device", originDeviceId), ("$instance", originInstanceId),
            ("$epoch", keyEpoch), ("$terminal", (byte)UserSyncHealthStatus.TerminalConflict));
    }

    public Task AddAsync(UserSyncFault fault, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        _rows.Stage(fault, SqliteMutation.Operation.Insert);
        return Task.CompletedTask;
    }

    public void Update(UserSyncFault fault) => _rows.Stage(fault, SqliteMutation.Operation.Update);

    internal async Task<UserSyncFault?> GetByIdAsync(Guid id, CancellationToken ct) =>
        (await session.ReadAsync($"""{Select} WHERE "Id" = $id;""", ReadForWrite, ct, ("$id", id))).SingleOrDefault();

    internal async Task<RecoveredFaultCompactionPage> ReadCompactionPageAsync(Guid? userId, string? afterKey, CancellationToken ct)
    {
        var rows = await session.ReadAsync("""
            SELECT "Id" FROM "UserSyncFaults" WHERE ($user IS NULL OR "UserId" = $user) AND ($after IS NULL OR "Id" > $after)
            ORDER BY "Id" LIMIT 9;
            """, row => row.GetString(0), ct, ("$user", userId), ("$after", afterKey));
        var candidates = new List<UserSyncFault>();
        foreach (var key in rows.Take(8))
        {
            if (!Guid.TryParse(key, out var id)) continue;
            try { if (await GetByIdAsync(id, ct) is { } fault) candidates.Add(fault); }
            catch (Exception ex) when (ex is InvalidDataException or FormatException or OverflowException or InvalidCastException)
            {
                // Malformed local metadata is retained. Its immutable key still advances the
                // read cursor, so it cannot starve unrelated, verifiable evidence forever.
            }
        }
        return new(candidates, rows.Count > 8 ? rows[7] : null);
    }

    internal void StageCompactedDiagnostics(UserSyncFault original)
    {
        if (!session.HasActiveTransaction) throw new InvalidOperationException("Fault compaction requires a transaction.");
        session.Stage<UserSyncFault>(SqliteMutation.Operation.Update, """
            UPDATE "UserSyncFaults" SET "DiagnosticCode" = ''
            WHERE "Id" = $id AND "UserId" = $user AND "Status" = $status AND "Scope" = $scope AND "Kind" = $kind
              AND "OriginDeviceId" = $device AND "OriginInstanceId" = $instance AND "KeyEpoch" = $epoch
              AND "MembershipEpoch" = $membership AND "OriginRevision" = $revision AND "SupersedingRevision" = $replacement
              AND "ObservedHash" = $observed AND "ConflictingHash" = $conflict AND "ExpectedHash" = $expected
              AND "AffectedComponent" = $component AND "DiagnosticCode" = $diagnostic
              AND "BlocksPublishing" = 0 AND "BlocksMerge" = 0 AND "BlocksLogin" = 0
              AND "BlocksGarbageCollection" = 0 AND "BlocksLifecycle" = 0;
            """, original.UserId, null, ("$id", original.Id), ("$user", original.UserId), ("$status", (byte)original.Status),
            ("$scope", (byte)original.Scope), ("$kind", (byte)original.Kind), ("$device", original.OriginDeviceId),
            ("$instance", original.OriginInstanceId), ("$epoch", original.KeyEpoch), ("$membership", original.MembershipEpoch),
            ("$revision", original.OriginRevision), ("$replacement", original.SupersedingRevision), ("$observed", original.ObservedHash),
            ("$conflict", original.ConflictingHash), ("$expected", original.ExpectedHash),
            ("$component", original.AffectedComponent), ("$diagnostic", original.DiagnosticCode));
    }

    public void DeleteRange(IEnumerable<UserSyncFault> faults)
    {
        foreach (var row in faults)
            _rows.Stage(row, SqliteMutation.Operation.Delete);
    }

    private async Task<List<UserSyncFault>> ReadEffectiveAsync(string predicate, Func<UserSyncFault, bool> matches,
        CancellationToken ct, params (string Name, object? Value)[] parameters)
    {
        var rows = await session.ReadAsync($"{Select} WHERE {predicate};", ReadForWrite, ct, parameters);
        var pending = new Dictionary<Guid, (UserSyncFault Row, SqliteMutation.Operation Operation)>();
        foreach (var item in session.GetPendingRows<UserSyncFault>())
            pending[item.Row.Id] = item;
        if (pending.Count == 0)
            return rows;

        rows.RemoveAll(row => pending.ContainsKey(row.Id));
        rows.AddRange(pending.Values.Where(item => item.Operation != SqliteMutation.Operation.Delete && matches(item.Row))
            .Select(item => item.Row));
        return rows;
    }

    private static bool IsActive(UserSyncFault row) =>
        row.Status is not (UserSyncHealthStatus.Recovered or UserSyncHealthStatus.Superseded or UserSyncHealthStatus.RetiredOrigin);

    private UserSyncFault ReadForWrite(SqliteDataReader row) => _rows.Remember(new UserSyncFault
    {
        Id = row.GetGuid(0),
        AffectedComponent = row.GetString(1),
        BlocksGarbageCollection = row.GetBoolean(2),
        BlocksLifecycle = row.GetBoolean(3),
        BlocksLogin = row.GetBoolean(4),
        BlocksMerge = row.GetBoolean(5),
        BlocksPublishing = row.GetBoolean(6),
        ConflictingHash = row.Blob(7),
        DiagnosticCode = row.GetString(8),
        ExpectedHash = row.Blob(9),
        FirstDetectedAtUtc = row.UtcOffset(10),
        KeyEpoch = row.NullableInt64(11),
        Kind = (UserSyncFaultKind)row.GetByte(12),
        LastDetectedAtUtc = row.UtcOffset(13),
        LastRecoveryAttemptAtUtc = row.NullableUtcOffset(14),
        MembershipEpoch = row.NullableInt64(15),
        NextRecoveryAttemptAtUtc = row.NullableUtcOffset(16),
        ObservedHash = row.Blob(17),
        OriginDeviceId = row.NullableGuid(18),
        OriginInstanceId = row.NullableGuid(19),
        OriginRevision = row.NullableInt64(20),
        RecoveredAtUtc = row.NullableUtcOffset(21),
        RecoveryAttemptCount = row.GetInt32(22),
        Scope = (UserSyncFaultScope)row.GetByte(23),
        Status = (UserSyncHealthStatus)row.GetByte(24),
        SupersedingRevision = row.NullableInt64(25),
        UserId = row.GetGuid(26)
    });
}
