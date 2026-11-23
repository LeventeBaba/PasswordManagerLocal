namespace PasswordManagerLocal.Common.Backend.Persistence;

/// <summary>
/// Conservative, read-only evidence inventory. No cross-origin coverage, age, recovered-fault
/// status or completed delivery is treated as permission to delete an exact signed proof.
/// </summary>
internal sealed class SqliteStorageRetentionAnalyzer(SqliteSession session)
{
    public Task<IReadOnlyList<StorageRetentionDecision>> AnalyzeAsync(CancellationToken ct = default) =>
        session.ReadSnapshotAsync<IReadOnlyList<StorageRetentionDecision>>(async () =>
        {
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
                result.Add(new(name, reader.GetInt64(1), reader.GetInt64(2), reason));
            }
            return result;
        }, ct);
}
