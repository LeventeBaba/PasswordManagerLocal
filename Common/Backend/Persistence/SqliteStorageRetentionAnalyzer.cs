using PasswordManagerLocal.Common.Backend.Models;
using PasswordManagerLocal.Common.Backend.Abstractions.Services;
using PasswordManagerLocal.Common.Backend.Repositories;
using PasswordManagerLocal.Common.Backend.Sync;
using System.Globalization;

namespace PasswordManagerLocal.Common.Backend.Persistence;

/// <summary>
/// Read-only inventory plus bounded, per-record authenticated duplicate-proof analysis.
/// Coverage, age and delivery completion never authorize removal of exact signed evidence.
/// </summary>
internal sealed class SqliteStorageRetentionAnalyzer(SqliteSession session,
    IUserMembershipAuthorizationService? membershipAuthorization = null)
{
    private sealed record EvidenceTable(string Name, string[] Keys, StorageRetentionReason Reason,
        string[] References, string? Status = null);

    private static readonly EvidenceTable[] Tables =
    [
        new("UserSyncSnapshots", ["Id"], StorageRetentionReason.RequiredBySnapshot,
            ["OriginDeviceId", "OriginInstanceId", "UserKeyEpoch", "OriginRevision", "MembershipEpoch", "SnapshotHash"], "Status"),
        new("UserRevisionKnowledge", ["UserId", "OriginDeviceId", "OriginInstanceId", "UserKeyEpoch"],
            StorageRetentionReason.RequiredByCausalCoverage, ["HighestStoredRevision", "HighestStoredSnapshotHash", "HighestMergedRevision"]),
        new("UserSyncStates", ["UserId"], StorageRetentionReason.RequiredByCausalCoverage, ["LocalOriginInstanceId", "NextOriginRevision"]),
        new("UserCanonicalCheckpoints", ["UserId"], StorageRetentionReason.RequiredByRecovery,
            ["CheckpointSequence", "KeyEpoch", "MembershipEpoch", "CanonicalContentHash"]),
        new("UserControlOperations", ["OperationId"], StorageRetentionReason.RequiredByAuthorization,
            ["OriginDeviceId", "OriginInstanceId", "OriginSequence", "PreviousKeyEpoch", "ResultingKeyEpoch",
             "PreviousMembershipEpoch", "ResultingMembershipEpoch", "OperationHash", "ConflictingOperationHash"], "Status"),
        new("UserControlStates", ["UserId"], StorageRetentionReason.RequiredByAuthorization,
            ["AppliedKeyEpoch", "AppliedMembershipEpoch", "ConflictingOperationId", "ConflictingOperationHash"], "HasConflict"),
        new("UserMembershipAuthorizations", ["AuthorizationId"], StorageRetentionReason.RequiredByAuthorization,
            ["DeviceId", "OriginInstanceId", "StartedMembershipEpoch", "EndedMembershipEpoch", "MinimumKeyEpoch", "MaximumKeyEpoch",
             "AdditionOperationId", "AdditionOperationHash", "RemovalOperationId", "RemovalOperationHash"]),
        new("UserOriginRemovalCutoffs", ["CutoffId"], StorageRetentionReason.RequiredByAuthorization,
            ["AuthorizationId", "RemovalOperationId", "RemovalOperationHash", "DeviceId", "OriginInstanceId",
             "UserKeyEpoch", "HighestAcceptedSnapshotRevision", "HighestAcceptedControlSequence"]),
        new("DeviceEnrollmentCommits", ["CommitId"], StorageRetentionReason.RequiredByAuthorization,
            ["AdditionOperationId", "AdditionOperationHash", "TargetDeviceId", "TargetOriginInstanceId"], "Status"),
        new("DeletedUserBarriers", ["UserId"], StorageRetentionReason.RequiredByRevocation,
            ["DeletionOperationId", "OperationHash", "DeletionGeneration", "OriginDeviceId", "OriginInstanceId", "OriginSequence"], "HasConflict"),
        new("RevocationReceiptEvidence", ["OperationId"], StorageRetentionReason.RequiredByRevocation,
            ["OperationHash", "HistoryResetBlocked"], "HasReceiptConflict"),
        new("RevocationDeliveryReceipts", ["OperationId", "RecipientDeviceId", "RecipientOriginInstanceId", "RecipientAuthorizationId", "Kind", "AttestedReceiptHash"],
            StorageRetentionReason.RequiredAsExactSignedReceipt, ["ReceiptHash"]),
        new("RevocationEvidenceDebts", ["OperationId", "PeerDeviceId", "PeerOriginInstanceId", "PeerAuthorizationId", "ReceiptHash"],
            StorageRetentionReason.RequiredByRevocation, []),
        new("UserSyncFaults", ["Id"], StorageRetentionReason.RequiredByRecovery,
            ["Scope", "Kind", "OriginDeviceId", "OriginInstanceId", "KeyEpoch", "MembershipEpoch", "OriginRevision",
             "ObservedHash", "ConflictingHash", "SupersedingRevision"], "Status"),
        new("UserRecoveryJournals", ["Id"], StorageRetentionReason.RequiredByRecovery, ["State", "CreatedAtUtc"]),
        new("CriticalControlIngress", ["EnvelopeHash"], StorageRetentionReason.RequiredByRevocation,
            ["OperationId", "OperationHash", "TransportPeerDeviceId"])
    ];

    internal static IReadOnlyList<string> EvidenceTables { get; } = Array.AsReadOnly(Tables.Select(table => table.Name).ToArray());

    public Task<IReadOnlyList<StorageRetentionDecision>> AnalyzeAsync(CancellationToken ct = default) =>
        session.ReadSnapshotAsync<IReadOnlyList<StorageRetentionDecision>>(async () =>
        {
            var findings = await AnalyzeEvidenceAsync(userId: null, ct);
            using var command = await session.CreateCommandAsync(
                "SELECT TableName, RowCount, UsedBytes FROM StorageUsage ORDER BY TableName;", ct);
            using var reader = await command.ExecuteReaderAsync(ct);
            var result = new List<StorageRetentionDecision>();
            while (await reader.ReadAsync(ct))
            {
                var name = reader.GetString(0);
                var policy = SqliteStoragePolicy.Tables.Single(table => table.TableName == name);
                var reason = policy.Category switch
                {
                    "Control" => StorageRetentionReason.RequiredByAuthorization,
                    "Revocation" or "Barriers" or "Ingress" => StorageRetentionReason.RequiredByRevocation,
                    "Recovery" => StorageRetentionReason.RequiredByRecovery,
                    "Snapshots" when name == "UserRevisionKnowledge" => StorageRetentionReason.RequiredByCausalCoverage,
                    "Snapshots" => StorageRetentionReason.RequiredBySnapshot,
                    _ => StorageRetentionReason.InsufficientProof
                };
                result.Add(new(name, reader.GetInt64(1), reader.GetInt64(2), reason)
                {
                    ReclaimableBytes = findings.Where(item => item.TableName == name).Sum(item => item.ReclaimableBytes)
                });
            }
            return result;
        }, ct);

    // One bounded page per category. AnalyzePageAsync supplies immutable-key continuation
    // for complete inspection without loading the whole history or decrypting user content.
    public Task<IReadOnlyList<StorageEvidenceRetentionDecision>> AnalyzeEvidenceAsync(
        Guid? userId, CancellationToken ct = default) =>
        session.ReadSnapshotAsync<IReadOnlyList<StorageEvidenceRetentionDecision>>(async () =>
        {
            var result = new List<StorageEvidenceRetentionDecision>();
            foreach (var table in Tables)
                result.AddRange((await AnalyzePageAsync(table.Name, userId, limit: 8, ct: ct)).Records);
            return result;
        }, ct);

    public Task<StorageEvidencePage> AnalyzePageAsync(string tableName, Guid? userId = null,
        StorageEvidenceCursor? after = null, int limit = 32, CancellationToken ct = default)
    {
        var table = Tables.SingleOrDefault(item => item.Name == tableName)
            ?? throw new ArgumentException("Unknown evidence table.", nameof(tableName));
        if (limit is < 1 or > 64) throw new ArgumentOutOfRangeException(nameof(limit));
        if (after is not null && (after.TableName != tableName || after.UserId != userId || after.Key.Count != table.Keys.Length ||
            after.Key.Any(value => value is not string && value is not long && value is not byte[])))
            throw new ArgumentException("The evidence cursor does not match this table and user scope.", nameof(after));
        // Crypto-bearing categories receive a smaller bound than metadata-only pages.
        if (tableName is "UserSyncFaults" or "RevocationReceiptEvidence") limit = Math.Min(limit, 8);
        return session.ReadSnapshotAsync(async () =>
        {
            static string Column(string name) => "e.\"" + name + "\"";
            var keys = table.Keys.Select(Column).ToArray();
            var debt = tableName == "RevocationEvidenceDebts";
            var user = debt ? "c.\"UserId\"" : "e.\"UserId\"";
            var from = "\"" + tableName + "\" e" + (debt ?
                " LEFT JOIN \"RevocationReceiptEvidence\" c ON c.\"OperationId\" = e.\"OperationId\"" : "");
            var status = table.Status is null ? "NULL" : Column(table.Status);
            var columns = keys.Concat([user, status]).Concat(table.References.Select(Column)).ToList();
            if (tableName == "RevocationReceiptEvidence")
                columns.Add("length(e.\"EnvelopePayload\") + length(e.\"AuthorizationPayload\") + length(e.\"MembershipProofPayload\")");
            var parameters = new List<(string Name, object? Value)> { ("$user", userId), ("$limit", limit + 1) };
            var predicate = "($user IS NULL OR " + user + " = $user)";
            if (after is not null)
            {
                var names = Enumerable.Range(0, keys.Length).Select(index => "$key" + index).ToArray();
                predicate += " AND (" + string.Join(",", keys) + ") > (" + string.Join(",", names) + ")";
                parameters.AddRange(names.Select((name, index) => (name, (object?)after.Key[index])));
            }
            var rows = await session.ReadAsync("SELECT " + string.Join(",", columns) + " FROM " + from +
                " WHERE " + predicate + " ORDER BY " + string.Join(",", keys) + " LIMIT $limit;",
                row => Enumerable.Range(0, row.FieldCount).Select(row.GetValue).ToArray(), ct, parameters.ToArray());
            var records = new List<StorageEvidenceRetentionDecision>();
            long remainingBytes = 16 * 1024 * 1024;
            foreach (var row in rows.Take(limit))
            {
                var key = row.Take(keys.Length).ToArray();
                var owner = row[keys.Length] is string id && Guid.TryParse(id, out var uid) ? uid : Guid.Empty;
                var recordId = key[0] is string first && Guid.TryParse(first, out var rid) ? rid : Guid.Empty;
                var reason = Classify(table, row[keys.Length + 1]);
                if (debt && owner == Guid.Empty) reason = StorageRetentionReason.InsufficientProof;
                var dependencies = table.References.Select((name, index) => name + "=" + Format(row[keys.Length + 2 + index])).ToArray();
                var decision = new StorageEvidenceRetentionDecision(tableName, recordId, owner, reason);
                try
                {
                    if (tableName == "RevocationReceiptEvidence" && row[^1] is long bytes && bytes <= remainingBytes)
                    {
                        remainingBytes -= bytes;
                        var evidence = await new RevocationReceiptRepository(session).GetEvidenceAsync(recordId, ct);
                        if (evidence is not null) decision = RevocationEvidenceCompactionPolicy.Describe(evidence);
                    }
                    else if (tableName == "UserSyncFaults")
                    {
                        var fault = await new UserSyncFaultRepository(session).GetByIdAsync(recordId, ct);
                        if (fault is not null) decision = await RecoveredOriginFaultCompactionPolicy.AnalyzeAsync(session, fault, membershipAuthorization, ct);
                    }
                }
                catch (Exception ex) when (ex is InvalidDataException or FormatException or OverflowException or InvalidCastException)
                {
                    decision = decision with { Reason = StorageRetentionReason.InsufficientProof, ReclaimableBytes = 0 };
                }
                records.Add(decision with { ImmutableIdentity = string.Join("/", key.Select(Format)), Dependencies = dependencies });
            }
            return new StorageEvidencePage(records, rows.Count > limit
                ? new(tableName, userId, rows[limit - 1].Take(keys.Length).ToArray()) : null);
        }, ct);
    }

    private static string Format(object value) => value switch
    {
        DBNull => "missing",
        byte[] bytes => Convert.ToHexString(bytes),
        _ => Convert.ToString(value, CultureInfo.InvariantCulture) ?? "missing"
    };

    private static StorageRetentionReason Classify(EvidenceTable table, object status)
    {
        var value = status is long number ? number : -1;
        return table.Name switch
        {
            "UserSyncSnapshots" => (UserSyncSnapshotStatus)value switch
            {
                UserSyncSnapshotStatus.MergedReceipt => StorageRetentionReason.RequiredAsExactSignedReceipt,
                UserSyncSnapshotStatus.IsolatedFork or UserSyncSnapshotStatus.RetiredForkEvidence => StorageRetentionReason.RequiredForForkDetection,
                UserSyncSnapshotStatus.Pending => StorageRetentionReason.BlockedByPendingEvidence,
                UserSyncSnapshotStatus.RecoveryCandidate or UserSyncSnapshotStatus.IsolatedCorrupt => StorageRetentionReason.RequiredByRecovery,
                _ => table.Reason
            },
            "UserControlOperations" when value == (long)UserControlOperationStatus.Quarantined => StorageRetentionReason.BlockedByConflict,
            "UserControlOperations" when value == (long)UserControlOperationStatus.StoredPending => StorageRetentionReason.BlockedByPendingEvidence,
            "UserControlStates" or "DeletedUserBarriers" or "RevocationReceiptEvidence" when value == 1 => StorageRetentionReason.BlockedByConflict,
            "UserSyncFaults" when value == (long)UserSyncHealthStatus.TerminalConflict => StorageRetentionReason.RequiredForForkDetection,
            _ => table.Reason
        };
    }
}
