using Microsoft.Data.Sqlite;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using PasswordManagerLocal.Common.Backend.Persistence;
using PasswordManagerLocal.Common.Backend.Models;
using PasswordManagerLocal.Common.Backend.Sync;
using PasswordManagerLocal.Common.Contracts.Errors;
using PasswordManagerLocal.Common.Tests.TestInfrastructure.Storage;
using static PasswordManagerLocal.Common.Tests.TestInfrastructure.Storage.SqliteStorageTestCommands;

namespace PasswordManagerLocal.Common.Tests.Backend.Persistence;

[TestClass]
[TestCategory("Backend")]
[TestCategory("Integration")]
[TestCategory("StoragePhysical")]
[DoNotParallelize]
public sealed class SqliteBoundedVfsTests
{
    [TestMethod]
    public async Task NativeFull_RollsBackStagedRowsAndAccounting_WithoutPublishingAChange()
    {
        const long limit = 1024 * 1024;
        await using var fixture = await SqliteStorageFileFixture.CreateAsync(
            walPressureBytes: 4 * limit, physicalLimits: new(WalBytes: limit));
        var devices = Enumerable.Range(0, 32).Select(_ => Guid.NewGuid()).ToArray();
        foreach (var id in devices) await InsertRowAsync(fixture.Anchor, "Devices", id);
        var ledger = await ScalarAsync(fixture.Anchor, "SELECT SUM(UsedBytes) FROM StorageBudgets;");
        await SqliteStorageMeasurement.CheckpointAsync(fixture.Factory, truncate: true);
        using var hub = new BackendChangeEventHub(TimeProvider.System);
        using var session = new SqliteSession(fixture.Factory, hub);
        SqliteStorageBudgetTests.StageWork(session);
        // Each staged mutation owns exactly one row. The complete flush must exceed the
        // native WAL allowance and roll back, whether SQLite spills during flush or at commit.
        foreach (var id in devices)
            session.Stage<Device>(SqliteMutation.Operation.Update,
                "UPDATE Devices SET PublicKey = zeroblob(60000) WHERE Id = $id;", null, null, ("$id", id.ToString()));
        var failure = await Assert.ThrowsExactlyAsync<StorageCapacityExceededException>(() => session.SaveChangesAsync());
        Assert.IsTrue(failure.InnerException is SqliteException { SqliteErrorCode: 13 },
            "The flush must reach native SQLITE_FULL rather than a logical budget or row-contract rejection.");
        Assert.AreEqual(0L, hub.CurrentPosition.Sequence);
        Assert.AreEqual(0L, await ScalarAsync(fixture.Anchor, "SELECT COUNT(*) FROM SyncItems;"));
        Assert.AreEqual(32L, await ScalarAsync(fixture.Anchor, "SELECT COUNT(*) FROM Devices WHERE length(PublicKey) = 32;"));
        Assert.AreEqual(ledger, await ScalarAsync(fixture.Anchor, "SELECT SUM(UsedBytes) FROM StorageBudgets;"));
        Assert.IsTrue(new FileInfo(fixture.DatabasePath + "-wal").Length <= limit);
    }

    [TestMethod]
    public async Task ReducedMainLimit_AlsoLimitsCommittedPagesStillInWal()
    {
        const long limit = 1024 * 1024;
        await using var fixture = await SqliteStorageFileFixture.CreateAsync(physicalLimits: new(MainBytes: limit));
        await InsertRowAsync(fixture.Anchor, "Devices", Guid.NewGuid());
        await ExecuteAsync(fixture.Anchor, "CREATE TABLE MainLimitProbe (Payload BLOB);");
        var failure = await Assert.ThrowsExactlyAsync<SqliteException>(() =>
            ExecuteAsync(fixture.Anchor, "INSERT INTO MainLimitProbe VALUES (zeroblob(2097152));"));
        Assert.IsTrue(SqliteCapacityError.IsCapacityFailure(failure));
        var pages = await ScalarAsync(fixture.Anchor, "PRAGMA page_count;");
        var pageSize = await ScalarAsync(fixture.Anchor, "PRAGMA page_size;");
        Assert.IsTrue(pages * pageSize <= limit);
        Assert.AreEqual(0L, await ScalarAsync(fixture.Anchor, "SELECT COUNT(*) FROM MainLimitProbe;"));
        Assert.AreEqual(1L, await ScalarAsync(fixture.Anchor, "SELECT COUNT(*) FROM Devices;"));
        await SqliteStorageMeasurement.CheckpointAsync(fixture.Factory, truncate: true);
        Assert.IsTrue(new FileInfo(fixture.DatabasePath).Length <= limit);
    }

    [TestMethod]
    public async Task CommitOvershoot_IsStoppedByNativeWriteLimit_AndReopenPreservesCommittedRows()
    {
        const long limit = 1024 * 1024;
        // Keep dirty pages in memory until COMMIT to exercise the native commit boundary,
        // independently of cache size or platform-specific spill timing.
        await using var fixture = await SqliteStorageFileFixture.CreateAsync(
            walPressureBytes: 4 * limit, physicalLimits: new(WalBytes: limit));
        await InsertRowAsync(fixture.Anchor, "Devices", Guid.NewGuid());
        await ExecuteAsync(fixture.Anchor, "CREATE TABLE NativeOvershoot (Payload BLOB);");
        var before = await ScalarAsync(fixture.Anchor, "SELECT SUM(UsedBytes) FROM StorageBudgets;");
        var beforeValue = await ScalarAsync(fixture.Anchor, "SELECT InvalidSyncAttemptCount FROM Devices;");
        await SqliteStorageMeasurement.CheckpointAsync(fixture.Factory, truncate: true);
        await ExecuteAsync(fixture.Anchor, "PRAGMA cache_spill = OFF;");
        using (var transaction = fixture.Anchor.BeginTransaction(deferred: false))
        {
            using var command = fixture.Anchor.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = "UPDATE Devices SET InvalidSyncAttemptCount = 42; INSERT INTO NativeOvershoot VALUES (zeroblob(2097152));";
            await command.ExecuteNonQueryAsync();
            var failure = await Assert.ThrowsExactlyAsync<SqliteException>(() => transaction.CommitAsync());
            Assert.AreEqual(13, failure.SqliteErrorCode, "The failure must come from the native file allowance at COMMIT.");
            Assert.IsTrue(SqliteCapacityError.IsCapacityFailure(failure));
        }
        Assert.IsTrue(new FileInfo(fixture.DatabasePath + "-wal").Length <= limit);
        await using var reopened = fixture.Factory.CreateConnection();
        await reopened.OpenAsync();
        Assert.AreEqual(1L, await ScalarAsync(reopened, "SELECT COUNT(*) FROM Devices;"));
        Assert.AreEqual(beforeValue, await ScalarAsync(reopened, "SELECT InvalidSyncAttemptCount FROM Devices;"));
        Assert.AreEqual(0L, await ScalarAsync(reopened, "SELECT COUNT(*) FROM NativeOvershoot;"));
        Assert.AreEqual(before, await ScalarAsync(reopened, "SELECT SUM(UsedBytes) FROM StorageBudgets;"));
        Assert.AreEqual("ok", await TextAsync(reopened, "PRAGMA quick_check;"));
    }

    [TestMethod]
    public async Task RepeatedPinnedReaderCommits_ReachHardLimit_WithoutGrowthOnRetries_AndResumeAfterRelease()
    {
        const long limit = 1024 * 1024;
        await using var fixture = await SqliteStorageFileFixture.CreateAsync(
            walPressureBytes: 4 * limit, physicalLimits: new(WalBytes: limit));
        await InsertRowAsync(fixture.Anchor, "Devices", Guid.NewGuid());
        await SqliteStorageMeasurement.CheckpointAsync(fixture.Factory, truncate: true);
        await using var reader = fixture.Factory.CreateConnection();
        await reader.OpenAsync();
        long lastValue = 1;
        using (var snapshot = reader.BeginTransaction(deferred: true))
        {
            using var pin = reader.CreateCommand();
            pin.Transaction = snapshot;
            pin.CommandText = "SELECT COUNT(*) FROM Devices;";
            await pin.ExecuteScalarAsync();
            var rejected = false;
            for (var index = 0; index < 1024; index++)
            {
                try
                {
                    await ExecuteAsync(fixture.Anchor, $"UPDATE Devices SET InvalidSyncAttemptCount = {index + 2};");
                    lastValue = index + 2;
                }
                catch (SqliteException exception) when (SqliteCapacityError.IsCapacityFailure(exception))
                {
                    rejected = true;
                    break;
                }
            }
            Assert.IsTrue(rejected);
            for (var retry = 0; retry < 16; retry++)
            {
                var failure = await Assert.ThrowsExactlyAsync<SqliteException>(() =>
                    ExecuteAsync(fixture.Anchor, "UPDATE Devices SET InvalidSyncAttemptCount = 9999;"));
                Assert.IsTrue(SqliteCapacityError.IsCapacityFailure(failure));
                Assert.IsTrue(new FileInfo(fixture.DatabasePath + "-wal").Length <= limit);
                Assert.AreEqual(lastValue, await ScalarAsync(fixture.Anchor, "SELECT InvalidSyncAttemptCount FROM Devices;"));
            }
        }
        await SqliteStorageMeasurement.CheckpointAsync(fixture.Factory, truncate: true);
        await ExecuteAsync(fixture.Anchor, "UPDATE Devices SET InvalidSyncAttemptCount = 42;");
        Assert.AreEqual(42L, await ScalarAsync(fixture.Anchor, "SELECT InvalidSyncAttemptCount FROM Devices;"));
    }

    [TestMethod]
    public async Task RollbackJournalLimit_AbortsLargeRewrite_AndRetainsPrecedingImage()
    {
        const long limit = 256 * 1024;
        await using var fixture = await SqliteStorageFileFixture.CreateAsync(
            physicalLimits: new(JournalBytes: limit));
        await ExecuteAsync(fixture.Anchor,
            "PRAGMA journal_mode = DELETE; CREATE TABLE RewriteData (Payload BLOB); INSERT INTO RewriteData VALUES (zeroblob(1048576));");
        var failure = await Assert.ThrowsExactlyAsync<SqliteException>(() => ExecuteAsync(fixture.Anchor,
            "UPDATE RewriteData SET Payload = randomblob(1048576);"));
        Assert.IsTrue(SqliteCapacityError.IsCapacityFailure(failure));
        var journal = new FileInfo(fixture.DatabasePath + "-journal");
        Assert.IsTrue(!journal.Exists || journal.Length <= limit);
        Assert.AreEqual(1L, await ScalarAsync(fixture.Anchor, "SELECT Payload = zeroblob(1048576) FROM RewriteData;"));
        Assert.AreEqual("ok", await TextAsync(fixture.Anchor, "PRAGMA quick_check;"));
    }

    [TestMethod]
    public async Task TemporaryDatabaseLimit_RejectsSpill_AndReleasesAllowanceOnClose()
    {
        const long limit = 256 * 1024;
        await using var fixture = await SqliteStorageFileFixture.CreateAsync(
            physicalLimits: new(TemporaryBytes: limit));
        for (var attempt = 0; attempt < 3; attempt++)
        {
            await using var connection = fixture.Factory.CreateConnection();
            await connection.OpenAsync();
            await ExecuteAsync(connection, "PRAGMA temp_store = FILE; PRAGMA temp.cache_size = 2; CREATE TEMP TABLE Scratch (Payload BLOB);");
            var failure = await Assert.ThrowsExactlyAsync<SqliteException>(() => ExecuteAsync(connection,
                "INSERT INTO Scratch VALUES (zeroblob(1048576));"));
            Assert.IsTrue(SqliteCapacityError.IsCapacityFailure(failure));
            Assert.AreEqual(0L, await ScalarAsync(connection, "SELECT COUNT(*) FROM Scratch;"));
            await ExecuteAsync(connection, "INSERT INTO Scratch VALUES (zeroblob(4096));");
            Assert.AreEqual(1L, await ScalarAsync(connection, "SELECT COUNT(*) FROM Scratch;"));
        }
        Assert.AreEqual("ok", await TextAsync(fixture.Anchor, "PRAGMA quick_check;"));
    }

    [TestMethod]
    public async Task TemporaryAllowance_IsSharedByConcurrentHandles_AndReturnsAfterOwnerCloses()
    {
        const long limit = 256 * 1024;
        await using var fixture = await SqliteStorageFileFixture.CreateAsync(physicalLimits: new(TemporaryBytes: limit));
        await using var first = fixture.Factory.CreateConnection();
        await using var second = fixture.Factory.CreateConnection();
        await first.OpenAsync();
        await second.OpenAsync();
        const string setup = "PRAGMA temp_store = FILE; PRAGMA temp.cache_size = 2; CREATE TEMP TABLE Scratch (Payload BLOB);";
        await ExecuteAsync(first, setup);
        await ExecuteAsync(second, setup);
        await ExecuteAsync(first, "INSERT INTO Scratch VALUES (zeroblob(163840));");
        var failure = await Assert.ThrowsExactlyAsync<SqliteException>(() =>
            ExecuteAsync(second, "INSERT INTO Scratch VALUES (zeroblob(163840));"));
        Assert.IsTrue(SqliteCapacityError.IsCapacityFailure(failure));
        Assert.AreEqual(1L, await ScalarAsync(first, "SELECT COUNT(*) FROM Scratch;"));
        await first.CloseAsync();
        await ExecuteAsync(second, "INSERT INTO Scratch VALUES (zeroblob(163840));");
        Assert.AreEqual(1L, await ScalarAsync(second, "SELECT COUNT(*) FROM Scratch;"));
    }

    [TestMethod]
    public async Task SharedMemoryLimit_StopsPinnedWalIndexGrowth_AndPreservesTheLastCommit()
    {
        const long limit = 32 * 1024;
        await using var fixture = await SqliteStorageFileFixture.CreateAsync(
            physicalLimits: new(SharedMemoryBytes: limit));
        await InsertRowAsync(fixture.Anchor, "Devices", Guid.NewGuid());
        await SqliteStorageMeasurement.CheckpointAsync(fixture.Factory, truncate: true);
        await using var reader = fixture.Factory.CreateConnection();
        await reader.OpenAsync();
        using var snapshot = reader.BeginTransaction(deferred: true);
        using var pin = reader.CreateCommand();
        pin.Transaction = snapshot;
        pin.CommandText = "SELECT COUNT(*) FROM Devices;";
        await pin.ExecuteScalarAsync();
        long lastValue = await ScalarAsync(fixture.Anchor, "SELECT InvalidSyncAttemptCount FROM Devices;");
        var rejected = false;
        for (var index = 0; index < 8192; index++)
        {
            try
            {
                await ExecuteAsync(fixture.Anchor, $"UPDATE Devices SET InvalidSyncAttemptCount = {index + 2};");
                lastValue = index + 2;
            }
            catch (SqliteException exception) when (SqliteCapacityError.IsCapacityFailure(exception))
            {
                rejected = true;
                break;
            }
        }
        Assert.IsTrue(rejected);
        Assert.IsTrue(new FileInfo(fixture.DatabasePath + "-shm").Length <= limit);
        Assert.AreEqual(lastValue, await ScalarAsync(fixture.Anchor, "SELECT InvalidSyncAttemptCount FROM Devices;"));
    }

    [TestMethod]
    public async Task EncryptedVault_PooledReopenAndRekey_KeepCipherOverBoundedVfs()
    {
        var directory = Path.Combine(Path.GetTempPath(), "pml-native-encrypted-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, "vault # ő.db");
        var factory = new SqliteConnectionFactory(DatabaseEncryptionMigrator.CreateRuntimeConnectionString(path, "first-key"));
        try
        {
            await using (var connection = factory.CreateConnection())
            {
                await connection.OpenAsync();
                await ExecuteAsync(connection, "CREATE TABLE CipherProbe (Value TEXT); INSERT INTO CipherProbe VALUES ('retained');");
                await connection.CloseAsync();
                await connection.OpenAsync();
                Assert.AreEqual("retained", await TextAsync(connection, "SELECT Value FROM CipherProbe;"));
                await ExecuteAsync(connection, "PRAGMA rekey = 'second-key';");
            }
            SqliteConnection.ClearAllPools();
            var updated = new SqliteConnectionFactory(DatabaseEncryptionMigrator.CreateRuntimeConnectionString(path, "second-key"));
            await using (var reopened = updated.CreateConnection())
            {
                await reopened.OpenAsync();
                Assert.AreEqual("retained", await TextAsync(reopened, "SELECT Value FROM CipherProbe;"));
                Assert.AreEqual("ok", await TextAsync(reopened, "PRAGMA quick_check;"));
            }
            // A disposed pooled connection can retain its native file handle. Release the
            // pool before inspecting ciphertext through an ordinary Windows file handle.
            SqliteConnection.ClearAllPools();
            Assert.AreNotEqual("SQLite format 3", System.Text.Encoding.ASCII.GetString(File.ReadAllBytes(path), 0, 15));
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            Directory.Delete(directory, recursive: true);
        }
    }

    [TestMethod]
    public async Task IncrementalReclamation_ReducesUnusedPages_AndPreservesRetainedEvidence()
    {
        await using var fixture = await SqliteStorageFileFixture.CreateAsync();
        Assert.AreEqual(2L, await ScalarAsync(fixture.Anchor, "PRAGMA auto_vacuum;"));
        await InsertRowAsync(fixture.Anchor, "DeletedUserBarriers", Guid.NewGuid());
        for (var index = 0; index < 256; index++) await InsertRowAsync(fixture.Anchor, "Devices", Guid.NewGuid());
        await ExecuteAsync(fixture.Anchor, "DELETE FROM Devices;");
        var before = await ScalarAsync(fixture.Anchor, "PRAGMA page_count;");
        var free = await ScalarAsync(fixture.Anchor, "PRAGMA freelist_count;");
        Assert.IsTrue(free > 0);
        var ledger = await ScalarAsync(fixture.Anchor, "SELECT SUM(UsedBytes) FROM StorageBudgets;");
        await SqliteStorageMaintenance.TryReclaimFreePagesAsync(fixture.Anchor, CancellationToken.None);
        Assert.IsTrue(await ScalarAsync(fixture.Anchor, "PRAGMA page_count;") < before);
        Assert.AreEqual(1L, await ScalarAsync(fixture.Anchor, "SELECT COUNT(*) FROM DeletedUserBarriers;"));
        Assert.AreEqual(ledger, await ScalarAsync(fixture.Anchor, "SELECT SUM(UsedBytes) FROM StorageBudgets;"));
        Assert.AreEqual("ok", await TextAsync(fixture.Anchor, "PRAGMA quick_check;"));
        await SqliteStorageMeasurement.CheckpointAsync(fixture.Factory, truncate: true);
    }

    [TestMethod]
    public async Task ExistingNonIncrementalImage_IsKeptUsableWithoutAnAutomaticFullRewrite()
    {
        await using var fixture = await SqliteStorageFileFixture.CreateAsync();
        // Model an older SQLite image without pointer-map pages. Only this test explicitly
        // rewrites its disposable image to create that format.
        await ExecuteAsync(fixture.Anchor, "PRAGMA journal_mode = DELETE; PRAGMA auto_vacuum = NONE; VACUUM; PRAGMA journal_mode = WAL;");
        await InsertRowAsync(fixture.Anchor, "DeletedUserBarriers", Guid.NewGuid());
        Assert.AreEqual(0L, await ScalarAsync(fixture.Anchor, "PRAGMA auto_vacuum;"));
        await AppDatabaseInitializer.InitializeAsync(fixture.Anchor);
        Assert.AreEqual(0L, await ScalarAsync(fixture.Anchor, "PRAGMA auto_vacuum;"));
        Assert.AreEqual(1L, await ScalarAsync(fixture.Anchor, "SELECT COUNT(*) FROM DeletedUserBarriers;"));
        Assert.AreEqual("ok", await TextAsync(fixture.Anchor, "PRAGMA quick_check;"));
    }

    private static async Task<string> TextAsync(SqliteConnection connection, string sql)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        return (string)(await command.ExecuteScalarAsync())!;
    }
}
