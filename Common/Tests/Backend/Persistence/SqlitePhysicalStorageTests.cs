using Microsoft.Data.Sqlite;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using PasswordManagerLocal.Common.Backend.Constants;
using PasswordManagerLocal.Common.Backend.Persistence;
using PasswordManagerLocal.Common.Tests.TestInfrastructure.Storage;
using static PasswordManagerLocal.Common.Tests.TestInfrastructure.Storage.SqliteStorageTestCommands;

namespace PasswordManagerLocal.Common.Tests.Backend.Persistence;

[TestClass]
[TestCategory("Backend")]
[TestCategory("Integration")]
[TestCategory("StoragePhysical")]
public sealed class SqlitePhysicalStorageTests
{
    public TestContext TestContext { get; set; } = null!;

    [TestMethod]
    public async Task EveryOpenedHandle_ReappliesPageCheckpointAndTemporaryStoragePolicy()
    {
        await using var fixture = await SqliteStorageFileFixture.CreateAsync();
        await using var connection = fixture.Factory.CreateConnection();
        await connection.OpenAsync();
        Assert.AreEqual(1L, await ScalarAsync(connection, "PRAGMA temp_store;"));
        Assert.AreEqual(1L, await ScalarAsync(connection, "PRAGMA recursive_triggers;"));
        Assert.AreEqual((long)StorageCapacityConstants.WalAutoCheckpointPages, await ScalarAsync(connection, "PRAGMA wal_autocheckpoint;"));
        Assert.AreEqual(StorageCapacityConstants.RetainedJournalBytes, await ScalarAsync(connection, "PRAGMA journal_size_limit;"));
        var sample = await SqliteStorageMeasurement.CaptureAsync(fixture.Factory, fixture.DatabasePath);
        sample.WriteTo(TestContext, "configured-capacity");
        Assert.IsTrue(sample.MaximumPageCount * sample.PageSize <= StorageCapacityConstants.MainDatabaseBytes);
        await connection.CloseAsync();
        await connection.OpenAsync();
        Assert.AreEqual(StorageCapacityConstants.RetainedJournalBytes, await ScalarAsync(connection, "PRAGMA journal_size_limit;"));
    }

    [TestMethod]
    public async Task CompetingWriters_CannotSpendTheSameRemainingBudget()
    {
        await using var fixture = await SqliteStorageFileFixture.CreateAsync();
        await InsertRowAsync(fixture.Anchor, "SyncItems", Guid.NewGuid());
        var oneRow = await ScalarAsync(fixture.Anchor, "SELECT UsedBytes FROM StorageBudgets WHERE Category='Work';");
        await ExecuteAsync(fixture.Anchor, "DELETE FROM SyncItems;");
        await SetBudgetAsync(fixture.Anchor, "Work", oneRow);
        var arrived = 0;
        var ready = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var results = await Task.WhenAll(Enumerable.Range(0, 2).Select(_ => Task.Run(async () =>
        {
            await using var connection = fixture.Factory.CreateConnection();
            await connection.OpenAsync();
            if (Interlocked.Increment(ref arrived) == 2) ready.TrySetResult();
            await ready.Task.WaitAsync(TimeSpan.FromSeconds(5));
            try { await InsertRowAsync(connection, "SyncItems", Guid.NewGuid()); return true; }
            catch (SqliteException exception) when (SqliteCapacityError.IsCapacityFailure(exception)) { return false; }
        })));
        Assert.AreEqual(1, results.Count(success => success));
        Assert.AreEqual(1L, await ScalarAsync(fixture.Anchor, "SELECT COUNT(*) FROM SyncItems;"));
        Assert.AreEqual(oneRow, await ScalarAsync(fixture.Anchor, "SELECT UsedBytes FROM StorageBudgets WHERE Category='Work';"));
        // Reopen a connection and rerun startup: neither the reduced budget nor usage may reset.
        await using var reopened = fixture.Factory.CreateConnection();
        await reopened.OpenAsync();
        await AppDatabaseInitializer.InitializeAsync(reopened);
        Assert.AreEqual(oneRow, await ScalarAsync(reopened, "SELECT LimitBytes FROM StorageBudgets WHERE Category='Work';"));
    }

    [TestMethod]
    public async Task SqliteFull_RollsBackBlobAndLedger_WithoutAuthorizingDestructiveRecovery()
    {
        await using var fixture = await SqliteStorageFileFixture.CreateAsync();
        await InsertRowAsync(fixture.Anchor, "Devices", Guid.NewGuid());
        var used = await ScalarAsync(fixture.Anchor, "SELECT UsedBytes FROM StorageBudgets WHERE Category='Identity';");
        var pages = await ScalarAsync(fixture.Anchor, "PRAGMA page_count;");
        await ExecuteAsync(fixture.Anchor, "PRAGMA max_page_count = " + (pages + 2) + ";");
        var exception = await Assert.ThrowsExactlyAsync<SqliteException>(() => ExecuteAsync(fixture.Anchor,
            "UPDATE Devices SET PublicKey = zeroblob(60000);"));
        Assert.AreEqual(13, exception.SqliteErrorCode, "The test must exercise SQLITE_FULL, not a logical row guard.");
        Assert.IsTrue(SqliteCapacityError.IsCapacityFailure(exception));
        Assert.AreEqual(32L, await ScalarAsync(fixture.Anchor, "SELECT length(PublicKey) FROM Devices;"));
        Assert.AreEqual(used, await ScalarAsync(fixture.Anchor, "SELECT UsedBytes FROM StorageBudgets WHERE Category='Identity';"));
        Assert.IsTrue(await ScalarAsync(fixture.Anchor, "PRAGMA page_count;") <= pages + 2);
    }

    [TestMethod]
    public async Task PinnedReader_DemonstratesCheckpointLimit_AndTruncationSucceedsAfterRelease()
    {
        await using var fixture = await SqliteStorageFileFixture.CreateAsync();
        await InsertRowAsync(fixture.Anchor, "Devices", Guid.NewGuid());
        await SqliteStorageMeasurement.CheckpointAsync(fixture.Factory, truncate: true);
        await using var readerConnection = fixture.Factory.CreateConnection();
        await readerConnection.OpenAsync();
        using (var snapshot = readerConnection.BeginTransaction(deferred: true))
        {
            using (var command = readerConnection.CreateCommand())
            {
                command.Transaction = snapshot;
                command.CommandText = "SELECT COUNT(*) FROM Devices;";
                await command.ExecuteScalarAsync();
            }
            for (var index = 0; index < 64; index++)
            {
                using var command = fixture.Anchor.CreateCommand();
                command.CommandText = "UPDATE Devices SET InvalidSyncAttemptCount = $value;";
                command.Parameters.AddWithValue("$value", index % 2);
                await command.ExecuteNonQueryAsync();
            }
            var blocked = await SqliteStorageMeasurement.CheckpointAsync(fixture.Factory);
            Assert.IsTrue(blocked.LogFrames > blocked.CheckpointedFrames,
                $"Pinned reader must retain WAL frames: log={blocked.LogFrames}, checkpointed={blocked.CheckpointedFrames}.");
            (await SqliteStorageMeasurement.CaptureAsync(fixture.Factory, fixture.DatabasePath)).WriteTo(TestContext, "pinned-reader");
        }
        var complete = await SqliteStorageMeasurement.CheckpointAsync(fixture.Factory, truncate: true);
        Assert.AreEqual(0L, complete.Busy);
        Assert.AreEqual(0L, new FileInfo(fixture.DatabasePath + "-wal").Length);
        Assert.AreEqual(1L, await ScalarAsync(fixture.Anchor, "SELECT COUNT(*) FROM Devices;"));
    }

    [TestMethod]
    public async Task DeleteInsertCycles_ReusePages_WithoutAccumulatingLedgerRows()
    {
        await using var fixture = await SqliteStorageFileFixture.CreateAsync();
        long highWater = 0;
        for (var round = 0; round < 5; round++)
        {
            for (var index = 0; index < 100; index++) await InsertRowAsync(fixture.Anchor, "Devices", Guid.NewGuid());
            var sample = await SqliteStorageMeasurement.CaptureAsync(fixture.Factory, fixture.DatabasePath);
            if (round == 0) highWater = sample.PageCount;
            Assert.IsTrue(sample.PageCount <= highWater + 32, $"Unexpected physical growth in cycle {round}: pages={sample.PageCount}, baseline={highWater}");
            await ExecuteAsync(fixture.Anchor, "DELETE FROM Devices;");
            Assert.AreEqual(0L, await ScalarAsync(fixture.Anchor, "SELECT SUM(UsedBytes) FROM StorageBudgets;"));
        }
        await SqliteStorageMeasurement.CheckpointAsync(fixture.Factory, truncate: true);
        var final = await SqliteStorageMeasurement.CaptureAsync(fixture.Factory, fixture.DatabasePath);
        Assert.AreEqual((long)SqliteStoragePolicy.Tables.Count, final.Rows["StorageUsage"]);
        Assert.IsTrue(final.FreelistCount > 0);
        final.WriteTo(TestContext, "reuse-after-five-cycles");
    }
}
