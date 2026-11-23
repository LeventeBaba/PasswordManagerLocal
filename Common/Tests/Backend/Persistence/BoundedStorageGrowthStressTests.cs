using Microsoft.Data.Sqlite;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using PasswordManagerLocal.Common.Backend.Persistence;
using PasswordManagerLocal.Common.Tests.TestInfrastructure;
using PasswordManagerLocal.Common.Tests.TestInfrastructure.Storage;
using static PasswordManagerLocal.Common.Tests.TestInfrastructure.Storage.SqliteStorageTestCommands;

namespace PasswordManagerLocal.Common.Tests.Backend.Persistence;

[TestClass]
[TestCategory("Backend")]
[TestCategory("BoundedGrowth")]
[TestCategory("SyncStressExtended")]
public sealed class BoundedStorageGrowthStressTests
{
    [TestMethod]
    [Timeout(120_000)]
    public async Task HistoricalCollections_ReachFiniteBudgets_AndRepeatedRefusalsNeverAllocateMoreRows()
    {
        if (Environment.GetEnvironmentVariable("PASSWORDMANAGERLOCAL_SYNC_STRESS") != "1")
            Assert.Inconclusive("Set PASSWORDMANAGERLOCAL_SYNC_STRESS=1 to run bounded-history workloads.");
        const int seed = 1701;
        var random = new Random(seed);
        foreach (var table in new[] { "UserSyncSnapshots", "UserRevisionKnowledge", "UserControlOperations",
            "UserMembershipAuthorizations", "RevocationDeliveryReceipts", "RevocationEvidenceDebts", "UserSyncFaults", "UserRecoveryJournals" })
        {
            await using var vault = await SqliteTestVault.CreateAsync();
            await using var connection = vault.Connections.CreateConnection();
            await connection.OpenAsync();
            await ExecuteAsync(connection, "PRAGMA foreign_keys = OFF;");
            var policy = SqliteStoragePolicy.Tables.Single(policy => policy.TableName == table);
            await SetBudgetAsync(connection, policy.Category, 32 * 1024);
            var rejected = false;
            for (var round = 0; round < 1024; round++)
            {
                var bytes = new byte[16]; random.NextBytes(bytes);
                try
                {
                    // Keep control operations pending so historical active-base uniqueness is not the admission limit.
                    await InsertRowAsync(connection, table, new Guid(bytes), new Dictionary<string, object> { ["Status"] = 0L });
                }
                catch (SqliteException exception) when (SqliteCapacityError.IsCapacityFailure(exception)) { rejected = true; break; }
            }
            Assert.IsTrue(rejected, $"Unbounded history: table={table}, budget=32768, seed={seed}");
            var count = await ScalarAsync(connection, "SELECT COUNT(*) FROM " + Quote(table) + ";");
            var used = await ScalarAsync(connection, "SELECT SUM(UsedBytes) FROM StorageBudgets;");
            for (var round = 0; round < 128; round++)
            {
                var bytes = new byte[16]; random.NextBytes(bytes);
                var error = await Assert.ThrowsExactlyAsync<SqliteException>(() => InsertRowAsync(connection, table,
                    new Guid(bytes), new Dictionary<string, object> { ["Status"] = 0L }));
                Assert.IsTrue(SqliteCapacityError.IsCapacityFailure(error), $"table={table}, seed={seed}, refused={round}");
            }
            Assert.AreEqual(count, await ScalarAsync(connection, "SELECT COUNT(*) FROM " + Quote(table) + ";"), table);
            Assert.AreEqual(used, await ScalarAsync(connection, "SELECT SUM(UsedBytes) FROM StorageBudgets;"), table);
        }
    }
}
