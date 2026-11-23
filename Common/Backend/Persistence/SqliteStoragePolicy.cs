using System.Globalization;
using Microsoft.Data.Sqlite;
using PasswordManagerLocal.Common.Backend.Constants;
using PasswordManagerLocal.Common.Contracts.Errors;
using static PasswordManagerLocal.Common.Backend.Constants.StorageCapacityConstants;

namespace PasswordManagerLocal.Common.Backend.Persistence;

/// <summary>One admission boundary for staged repositories, immediate SQL, imports and cascades.</summary>
internal static class SqliteStoragePolicy
{
    internal static IReadOnlyDictionary<string, long> Budgets { get; } = new Dictionary<string, long>(StringComparer.Ordinal)
    {
        ["Content"] = ContentBytes, ["Snapshots"] = SnapshotBytes, ["Control"] = ControlBytes,
        ["Revocation"] = RevocationBytes, ["Barriers"] = BarrierBytes, ["Recovery"] = RecoveryBytes,
        ["Identity"] = IdentityBytes, ["Work"] = WorkBytes, ["Ingress"] = CriticalIngressBytes
    };

    internal static IReadOnlyList<SqliteStorageTablePolicy> Tables { get; } = Array.AsReadOnly<SqliteStorageTablePolicy>(
    [
        new("Users", "Content", MaxLargeRowBytes, MaxProfiles),
        new("UserDevices", "Content", MaxMetadataRowBytes),
        new("LocalUserDevices", "Content", MaxMetadataRowBytes),
        new("UserLoginIdentityStates", "Content", MaxMetadataRowBytes),
        new("Devices", "Identity", MaxMetadataRowBytes),
        new("LocalDeviceIdentity", "Identity", MaxMetadataRowBytes, 1),
        new("SyncItems", "Work", MaxMetadataRowBytes),
        new("SyncQueueItems", "Work", MaxMetadataRowBytes),
        new("SyncTombstones", "Work", MaxMetadataRowBytes, TombstoneConstants.MaxSyncTombstones),
        new("SyncVersionClockState", "Work", MaxMetadataRowBytes, 1),
        new("UserSyncStates", "Snapshots", MaxMetadataRowBytes),
        new("UserSyncSnapshots", "Snapshots", SyncConstants.MaxUserSnapshotEnvelopeBytes + MaxMetadataRowBytes),
        new("UserRevisionKnowledge", "Snapshots", MaxMetadataRowBytes),
        new("UserCanonicalCheckpoints", "Snapshots", MaxMetadataRowBytes),
        new("UserControlOperations", "Control", SyncConstants.MaxUserControlOperationEnvelopeBytes + MaxMetadataRowBytes),
        new("UserControlStates", "Control", MaxMetadataRowBytes),
        new("UserMembershipAuthorizations", "Control", MaxMetadataRowBytes),
        new("UserOriginRemovalCutoffs", "Control", MaxMetadataRowBytes),
        new("DeviceEnrollmentCommits", "Control", MaxMetadataRowBytes),
        new("DeletedUserBarriers", "Barriers", MaxMetadataRowBytes),
        new("RevocationReceiptEvidence", "Revocation", MaxLargeRowBytes),
        new("RevocationDeliveryReceipts", "Revocation", MaxMetadataRowBytes),
        new("RevocationEvidenceDebts", "Revocation", MaxMetadataRowBytes),
        new("UserSyncFaults", "Recovery", MaxMetadataRowBytes),
        new("UserRecoveryJournals", "Recovery", MaxLargeRowBytes),
        new("CriticalControlIngress", "Ingress", SyncConstants.MaxUserControlOperationEnvelopeBytes + MaxMetadataRowBytes, MaxCriticalIngressRows)
    ]);

    internal static IEnumerable<string> WalGuardedTables => Tables.Select(table => table.TableName)
        .Concat(["StorageBudgets", "StorageUsage"]);

    public static async Task InstallAsync(SqliteConnection connection, SqliteTransaction transaction, CancellationToken ct)
    {
        // Fail startup for an unclassified persistent table instead of silently leaving a new growth path open.
        var names = await ReadStringsAsync(connection, transaction,
            "SELECT name FROM sqlite_schema WHERE type = 'table' AND name NOT LIKE 'sqlite_%';", ct);
        var expected = Tables.Select(table => table.TableName).ToHashSet(StringComparer.Ordinal);
        expected.UnionWith(["StorageBudgets", "StorageUsage"]);
        if (names.Any(name => !expected.Contains(name)))
            throw new InvalidOperationException("A persistent table has no storage admission policy.");

        var installed = names.Contains("StorageUsage", StringComparer.Ordinal);
        if (installed)
        {
            await VerifyInstallationAsync(connection, transaction, ct);
            return;
        }

        var tableNames = string.Join(",", Tables.Select(table => Literal(table.TableName)));
        var categoryNames = string.Join(",", Budgets.Keys.Select(Literal));
        await ExecuteAsync(connection, transaction, $"""
            CREATE TABLE "StorageBudgets" (
                "Category" TEXT NOT NULL PRIMARY KEY CHECK("Category" IN ({categoryNames})),
                "LimitBytes" INTEGER NOT NULL CHECK("LimitBytes" > 0 AND "LimitBytes" <= 1073741824),
                "UsedBytes" INTEGER NOT NULL CHECK("UsedBytes" >= 0)
            );
            CREATE TABLE "StorageUsage" (
                "TableName" TEXT NOT NULL PRIMARY KEY CHECK("TableName" IN ({tableNames})),
                "UsedBytes" INTEGER NOT NULL CHECK("UsedBytes" >= 0),
                "RowCount" INTEGER NOT NULL CHECK("RowCount" >= 0)
            );
            """, ct);

        foreach (var (category, limit) in Budgets)
            await ExecuteAsync(connection, transaction, $"INSERT INTO StorageBudgets VALUES ({Literal(category)}, {Number(limit)}, 0);", ct);

        foreach (var policy in Tables)
        {
            var columns = new List<string>();
            using (var command = connection.CreateCommand())
            {
                command.Transaction = transaction;
                command.CommandText = $"PRAGMA table_info({Identifier(policy.TableName)});";
                using var reader = await command.ExecuteReaderAsync(ct);
                while (await reader.ReadAsync(ct)) columns.Add(reader.GetString(1));
            }
            if (columns.Count == 0)
                throw new InvalidOperationException("A storage-controlled table is missing.");

            // Include every column, including TEXT's UTF-8 bytes and any unexpected SQLite dynamic type.
            string Charge(string prefix) => Number(RowChargeBytes) + " + " + string.Join(" + ",
                columns.Select(column => $"COALESCE(length(CAST({prefix}{Identifier(column)} AS BLOB)), 0)"));
            var table = Identifier(policy.TableName);
            var key = Literal(policy.TableName);
            var category = Literal(policy.Category);
            var fresh = Charge("NEW.");
            var old = Charge("OLD.");
            var unqualified = Charge("");
            using (var validation = connection.CreateCommand())
            {
                validation.Transaction = transaction;
                validation.CommandText = $"SELECT EXISTS(SELECT 1 FROM {table} WHERE ({unqualified}) > {Number(policy.MaxRowBytes)});";
                if (Convert.ToInt64(await validation.ExecuteScalarAsync(ct), CultureInfo.InvariantCulture) != 0)
                    throw new StorageCapacityExceededException();
                if (policy.MaxRows is int rowLimit)
                {
                    validation.CommandText = $"SELECT COUNT(*) FROM {table};";
                    if (Convert.ToInt64(await validation.ExecuteScalarAsync(ct), CultureInfo.InvariantCulture) > rowLimit)
                        throw new StorageCapacityExceededException();
                }
            }
            await ExecuteAsync(connection, transaction, $"""
                INSERT INTO StorageUsage
                SELECT {key}, COALESCE(SUM({unqualified}), 0), COUNT(*) FROM {table};
                UPDATE StorageBudgets SET UsedBytes = UsedBytes + (SELECT UsedBytes FROM StorageUsage WHERE TableName = {key})
                WHERE Category = {category};
                """, ct);

            foreach (var operation in new[] { "INSERT", "UPDATE", "DELETE" })
            {
                var delta = operation switch { "INSERT" => fresh, "DELETE" => $"-({old})", _ => $"({fresh}) - ({old})" };
                var countDelta = operation switch { "INSERT" => 1, "DELETE" => -1, _ => 0 };
                var rowGuard = operation == "DELETE" ? "" : $"SELECT CASE WHEN ({fresh}) > {Number(policy.MaxRowBytes)} THEN RAISE(ABORT, '{SqliteCapacityError.Marker}{policy.TableName}:row') END;";
                var countGuard = policy.MaxRows is int maximum && operation == "INSERT"
                    ? $"SELECT CASE WHEN (SELECT RowCount FROM StorageUsage WHERE TableName = {key}) > {maximum} THEN RAISE(ABORT, '{SqliteCapacityError.Marker}{policy.TableName}:count') END;" : "";
                // AFTER triggers let an UPSERT update a full collection without requiring a spare slot.
                // RAISE(ABORT) undoes this statement and its accounting; the UoW rolls back its entire flush.
                await ExecuteAsync(connection, transaction, $"""
                    CREATE TRIGGER {Identifier(TriggerName(policy, operation))} AFTER {operation} ON {table}
                    BEGIN
                        {rowGuard}
                        UPDATE StorageUsage SET UsedBytes = UsedBytes + ({delta}), RowCount = RowCount + ({countDelta}) WHERE TableName = {key};
                        SELECT CASE WHEN changes() <> 1 THEN RAISE(ABORT, '{SqliteCapacityError.Marker}missing-usage') END;
                        {countGuard}
                        UPDATE StorageBudgets SET UsedBytes = UsedBytes + ({delta}) WHERE Category = {category};
                        SELECT CASE WHEN changes() <> 1 THEN RAISE(ABORT, '{SqliteCapacityError.Marker}missing-budget') END;
                        SELECT CASE WHEN (SELECT UsedBytes > LimitBytes FROM StorageBudgets WHERE Category = {category})
                            THEN RAISE(ABORT, '{SqliteCapacityError.Marker}{policy.Category}:bytes') END;
                    END;
                    """, ct);
            }
        }
        foreach (var name in WalGuardedTables)
        {
            foreach (var operation in new[] { "INSERT", "UPDATE", "DELETE" })
            {
                await ExecuteAsync(connection, transaction, $"""
                    CREATE TRIGGER {Identifier(WalTriggerName(name, operation))} BEFORE {operation} ON {Identifier(name)}
                    BEGIN
                        SELECT CASE WHEN {SqliteWalPressure.FunctionName}() IS NOT 1
                            THEN RAISE(ABORT, '{SqliteCapacityError.Marker}wal-pressure') END;
                    END;
                    """, ct);
            }
        }
        await VerifyInstallationAsync(connection, transaction, ct);
    }

    private static async Task VerifyInstallationAsync(SqliteConnection connection, SqliteTransaction transaction, CancellationToken ct)
    {
        var usage = await ReadStringsAsync(connection, transaction, "SELECT TableName FROM StorageUsage;", ct);
        var categories = await ReadStringsAsync(connection, transaction,
            "SELECT Category FROM StorageBudgets WHERE UsedBytes <= LimitBytes;", ct);
        var triggers = await ReadStringsAsync(connection, transaction,
            "SELECT name FROM sqlite_schema WHERE type = 'trigger';", ct);
        if (!usage.ToHashSet(StringComparer.Ordinal).SetEquals(Tables.Select(table => table.TableName)) ||
            !categories.ToHashSet(StringComparer.Ordinal).SetEquals(Budgets.Keys) ||
            Tables.Any(table => new[] { "INSERT", "UPDATE", "DELETE" }.Any(operation => !triggers.Contains(TriggerName(table, operation), StringComparer.Ordinal))) ||
            WalGuardedTables.Any(table => new[] { "INSERT", "UPDATE", "DELETE" }.Any(operation => !triggers.Contains(WalTriggerName(table, operation), StringComparer.Ordinal))))
            throw new InvalidOperationException("The vault storage admission schema is incomplete or over capacity. Existing data was preserved.");
    }

    private static string TriggerName(SqliteStorageTablePolicy policy, string operation) => $"Storage_{policy.TableName}_{operation}";
    private static string WalTriggerName(string table, string operation) => $"WalPressure_{table}_{operation}";
    private static string Identifier(string value) => "\"" + value.Replace("\"", "\"\"", StringComparison.Ordinal) + "\"";
    private static string Literal(string value) => "'" + value.Replace("'", "''", StringComparison.Ordinal) + "'";
    private static string Number(long value) => value.ToString(CultureInfo.InvariantCulture);

    private static async Task<List<string>> ReadStringsAsync(SqliteConnection connection, SqliteTransaction transaction, string sql, CancellationToken ct)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = sql;
        using var reader = await command.ExecuteReaderAsync(ct);
        var values = new List<string>();
        while (await reader.ReadAsync(ct)) values.Add(reader.GetString(0));
        return values;
    }

    private static async Task ExecuteAsync(SqliteConnection connection, SqliteTransaction transaction, string sql, CancellationToken ct)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = sql;
        await command.ExecuteNonQueryAsync(ct);
    }
}
