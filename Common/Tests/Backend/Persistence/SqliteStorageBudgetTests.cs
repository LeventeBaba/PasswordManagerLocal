using Microsoft.Data.Sqlite;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using PasswordManagerLocal.Common.Backend.Constants;
using PasswordManagerLocal.Common.Backend.Models;
using PasswordManagerLocal.Common.Backend.Persistence;
using PasswordManagerLocal.Common.Backend.Repositories;
using PasswordManagerLocal.Common.Backend.Sync;
using PasswordManagerLocal.Common.Contracts.Errors;
using PasswordManagerLocal.Common.Contracts.Notifications;
using PasswordManagerLocal.Common.Tests.TestInfrastructure;
using PasswordManagerLocal.Common.Tests.TestInfrastructure.Storage;
using static PasswordManagerLocal.Common.Tests.TestInfrastructure.Storage.SqliteStorageTestCommands;

namespace PasswordManagerLocal.Common.Tests.Backend.Persistence;

[TestClass]
[TestCategory("Backend")]
[TestCategory("Integration")]
[TestCategory("StorageBudget")]
public sealed class SqliteStorageBudgetTests
{
    [TestMethod]
    public async Task EveryPersistentTable_HasLogicalAndWalGuards_AndExhaustionPreservesAllAccounting()
    {
        await using var vault = await SqliteTestVault.CreateAsync();
        await using var connection = vault.Connections.CreateConnection();
        await connection.OpenAsync();
        // These are raw structural rows; service-level tests below verify cryptographic admissions.
        await ExecuteAsync(connection, "PRAGMA foreign_keys = OFF;");
        var tableCount = await ScalarAsync(connection,
            "SELECT COUNT(*) FROM sqlite_schema WHERE type='table' AND name NOT LIKE 'sqlite_%';");
        Assert.AreEqual(SqliteStoragePolicy.Tables.Count + 2L, tableCount, "Every new table needs a capacity policy.");
        Assert.AreEqual(SqliteStoragePolicy.Tables.Count * 3L, await ScalarAsync(connection,
            "SELECT COUNT(*) FROM sqlite_schema WHERE type='trigger' AND name LIKE 'Storage_%';"));
        Assert.AreEqual((SqliteStoragePolicy.Tables.Count + 2L) * 3, await ScalarAsync(connection,
            "SELECT COUNT(*) FROM sqlite_schema WHERE type='trigger' AND name LIKE 'WalPressure_%';"));
        foreach (var policy in SqliteStoragePolicy.Tables)
        {
            Assert.IsTrue(policy.MaxRowBytes > StorageCapacityConstants.RowChargeBytes);
            await SetBudgetAsync(connection, policy.Category, 1);
            var error = await Assert.ThrowsExactlyAsync<SqliteException>(() => InsertRowAsync(connection, policy.TableName, Guid.NewGuid()));
            Assert.IsTrue(SqliteCapacityError.IsCapacityFailure(error), $"Missing guard: {policy.TableName}, {policy.Category}");
            Assert.AreEqual(0L, await ScalarAsync(connection, "SELECT COALESCE(SUM(RowCount), 0) FROM StorageUsage;"), policy.TableName);
            Assert.AreEqual(0L, await ScalarAsync(connection, "SELECT SUM(UsedBytes) FROM StorageBudgets;"), policy.TableName);
            await SetBudgetAsync(connection, policy.Category, SqliteStoragePolicy.Budgets[policy.Category]);
        }
    }

    [TestMethod]
    public async Task FlushOverBudget_RollsBackEarlierRowsAccountingAndNotifications()
    {
        await using var vault = await SqliteTestVault.CreateAsync();
        await using var connection = vault.Connections.CreateConnection();
        await connection.OpenAsync();
        await SetBudgetAsync(connection, "Work", 500);
        using var hub = new BackendChangeEventHub(TimeProvider.System);
        using var session = new SqliteSession(vault.Connections, hub);
        StageWork(session); StageWork(session); StageWork(session);
        await Assert.ThrowsExactlyAsync<StorageCapacityExceededException>(() => session.SaveChangesAsync());
        Assert.AreEqual(0L, await ScalarAsync(connection, "SELECT COUNT(*) FROM SyncItems;"));
        Assert.AreEqual(0L, await ScalarAsync(connection, "SELECT SUM(UsedBytes) FROM StorageBudgets;"));
        Assert.AreEqual(0L, hub.CurrentPosition.Sequence);
        session.ClearTrackedChanges();
        StageWork(session);
        await session.SaveChangesAsync();
        Assert.AreEqual(1L, await ScalarAsync(connection, "SELECT COUNT(*) FROM SyncItems;"));
        Assert.AreEqual(1L, hub.CurrentPosition.Sequence);
    }

    [TestMethod]
    public async Task SavepointExhaustion_KeepsEarlierFlush_AndOuterRollbackRestoresCounters()
    {
        await using var vault = await SqliteTestVault.CreateAsync();
        await using var connection = vault.Connections.CreateConnection();
        await connection.OpenAsync();
        await SetBudgetAsync(connection, "Work", 500);
        using var session = new SqliteSession(vault.Connections);
        await using var transaction = await session.BeginTransactionAsync();
        StageWork(session);
        await session.SaveChangesAsync();
        StageWork(session); StageWork(session);
        await Assert.ThrowsExactlyAsync<StorageCapacityExceededException>(() => session.SaveChangesAsync());
        using (var count = await session.CreateCommandAsync("SELECT COUNT(*) FROM SyncItems;"))
            Assert.AreEqual(1L, Convert.ToInt64(await count.ExecuteScalarAsync()));
        session.ClearTrackedChanges();
        await transaction.RollbackAsync();
        Assert.AreEqual(0L, await ScalarAsync(connection, "SELECT SUM(UsedBytes) FROM StorageBudgets;"));
    }

    [TestMethod]
    public async Task Utf8AndBlobGrowth_IsCharged_AndRejectedUpdatePreservesPriorValue()
    {
        await using var vault = await SqliteTestVault.CreateAsync();
        await using var connection = vault.Connections.CreateConnection();
        await connection.OpenAsync();
        await InsertRowAsync(connection, "Devices", Guid.NewGuid());
        var used = await ScalarAsync(connection, "SELECT UsedBytes FROM StorageBudgets WHERE Category='Identity';");
        await SetBudgetAsync(connection, "Identity", used);
        var failure = await Assert.ThrowsExactlyAsync<SqliteException>(() => ExecuteAsync(connection,
            "UPDATE Devices SET BlockedReason = '" + new string('\u00e9', 100) + "';"));
        Assert.IsTrue(SqliteCapacityError.IsCapacityFailure(failure));
        Assert.AreEqual(used, await ScalarAsync(connection, "SELECT UsedBytes FROM StorageBudgets WHERE Category='Identity';"));
        await SetBudgetAsync(connection, "Identity", used + 1024);
        await ExecuteAsync(connection, "UPDATE Devices SET BlockedReason = '" + new string('\u00e9', 100) + "';");
        // The original structural text value is a 36-byte GUID; the replacement is 200 UTF-8 bytes.
        Assert.AreEqual(used + 200 - 36, await ScalarAsync(connection, "SELECT UsedBytes FROM StorageBudgets WHERE Category='Identity';"));
        await Assert.ThrowsExactlyAsync<SqliteException>(() => ExecuteAsync(connection, "UPDATE Devices SET PublicKey = zeroblob(65536);"));
        await ExecuteAsync(connection, "DELETE FROM Devices;");
        Assert.AreEqual(0L, await ScalarAsync(connection, "SELECT SUM(UsedBytes) FROM StorageBudgets;"));
    }

    [TestMethod]
    public async Task FullCollection_AllowsIdempotentUpsert_AndDeleteReleasesCapacity()
    {
        await using var vault = await SqliteTestVault.CreateAsync();
        using var session = new SqliteSession(vault.Connections);
        var tombstones = new SyncTombstoneRepository(session);
        var id = Guid.NewGuid();
        await tombstones.UpsertAsync(id, SyncModelType.User, 1);
        await session.SaveChangesAsync();
        await using var connection = vault.Connections.CreateConnection();
        await connection.OpenAsync();
        var used = await ScalarAsync(connection, "SELECT UsedBytes FROM StorageBudgets WHERE Category='Work';");
        await SetBudgetAsync(connection, "Work", used);
        await tombstones.UpsertAsync(id, SyncModelType.User, 2);
        await session.SaveChangesAsync();
        Assert.AreEqual(used, await ScalarAsync(connection, "SELECT UsedBytes FROM StorageBudgets WHERE Category='Work';"));
        await tombstones.UpsertAsync(Guid.NewGuid(), SyncModelType.User, 1);
        await Assert.ThrowsExactlyAsync<StorageCapacityExceededException>(() => session.SaveChangesAsync());
        session.ClearTrackedChanges();
        tombstones.Delete((await tombstones.GetAsync(id, SyncModelType.User))!);
        await session.SaveChangesAsync();
        Assert.AreEqual(0L, await ScalarAsync(connection, "SELECT UsedBytes FROM StorageBudgets WHERE Category='Work';"));
    }

    [TestMethod]
    public async Task IndependentReserve_AndReadOnlyAnalyzer_PreservePermanentBarrier()
    {
        await using var vault = await SqliteTestVault.CreateAsync();
        await using var connection = vault.Connections.CreateConnection();
        await connection.OpenAsync();
        await SetBudgetAsync(connection, "Content", 1);
        await Assert.ThrowsExactlyAsync<SqliteException>(() => InsertRowAsync(connection, "Users", Guid.NewGuid()));
        await InsertRowAsync(connection, "DeletedUserBarriers", Guid.NewGuid());
        using var session = new SqliteSession(vault.Connections);
        var before = await ScalarAsync(connection, "SELECT SUM(UsedBytes) FROM StorageBudgets;");
        var decisions = await new SqliteStorageRetentionAnalyzer(session).AnalyzeAsync();
        var barrier = decisions.Single(item => item.TableName == "DeletedUserBarriers");
        Assert.AreEqual(StorageRetentionReason.RequiredByRevocation, barrier.Reason);
        Assert.AreEqual(1L, barrier.RowCount);
        Assert.IsTrue(decisions.All(item => item.ReclaimableBytes == 0));
        Assert.AreEqual(before, await ScalarAsync(connection, "SELECT SUM(UsedBytes) FROM StorageBudgets;"));
        Assert.AreEqual(1L, await ScalarAsync(connection, "SELECT COUNT(*) FROM DeletedUserBarriers;"));
    }

    [TestMethod]
    public async Task Cascades_ReleaseBothTablesAccounting_AndStartupNeverResetsUsage()
    {
        await using var vault = await SqliteTestVault.CreateAsync();
        await using var connection = vault.Connections.CreateConnection(enforceForeignKeys: true);
        await connection.OpenAsync();
        var id = Guid.NewGuid();
        await InsertRowAsync(connection, "Users", id);
        await InsertRowAsync(connection, "UserSyncStates", id);
        var before = await ScalarAsync(connection, "SELECT SUM(UsedBytes) FROM StorageBudgets;");
        await AppDatabaseInitializer.InitializeAsync(connection);
        Assert.AreEqual(before, await ScalarAsync(connection, "SELECT SUM(UsedBytes) FROM StorageBudgets;"));
        await ExecuteAsync(connection, "DELETE FROM Users;");
        Assert.AreEqual(0L, await ScalarAsync(connection, "SELECT SUM(UsedBytes) FROM StorageBudgets;"));
        Assert.AreEqual(0L, await ScalarAsync(connection, "SELECT SUM(RowCount) FROM StorageUsage;"));
    }

    internal static void StageWork(SqliteSession session)
    {
        var id = Guid.NewGuid();
        var changes = new BackendChangeSet();
        changes.Add(id, FrontendChangeScope.Passwords);
        session.Stage<SyncItem>(SqliteMutation.Operation.Insert, """
            INSERT INTO SyncItems (Id, ChangeType, ChangedAtTs, ModelId, ModelType) VALUES ($id, 1, 1, $id, 1);
            """, null, changes, ("$id", id));
    }

    [TestMethod]
    public async Task ProfileCount_StopsAtProtocolCompatibleLimit_WithoutDeletingExistingProfiles()
    {
        await using var vault = await SqliteTestVault.CreateAsync();
        await using var connection = vault.Connections.CreateConnection();
        await connection.OpenAsync();
        for (var index = 0; index < StorageCapacityConstants.MaxProfiles; index++)
            await InsertRowAsync(connection, "Users", Guid.NewGuid());
        var used = await ScalarAsync(connection, "SELECT UsedBytes FROM StorageBudgets WHERE Category='Content';");
        var error = await Assert.ThrowsExactlyAsync<SqliteException>(() => InsertRowAsync(connection, "Users", Guid.NewGuid()));
        Assert.IsTrue(SqliteCapacityError.IsCapacityFailure(error));
        Assert.AreEqual((long)StorageCapacityConstants.MaxProfiles, await ScalarAsync(connection, "SELECT COUNT(*) FROM Users;"));
        Assert.AreEqual(used, await ScalarAsync(connection, "SELECT UsedBytes FROM StorageBudgets WHERE Category='Content';"));
    }
}
