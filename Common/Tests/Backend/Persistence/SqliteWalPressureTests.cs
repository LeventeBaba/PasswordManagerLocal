using Microsoft.Data.Sqlite;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using PasswordManagerLocal.Common.Backend.Constants;
using PasswordManagerLocal.Common.Backend.Models;
using PasswordManagerLocal.Common.Backend.Persistence;
using PasswordManagerLocal.Common.Backend.Sync;
using PasswordManagerLocal.Common.Contracts.Errors;
using PasswordManagerLocal.Common.Tests.TestInfrastructure.Storage;
using static PasswordManagerLocal.Common.Tests.TestInfrastructure.Storage.SqliteStorageTestCommands;

namespace PasswordManagerLocal.Common.Tests.Backend.Persistence;

[TestClass]
[TestCategory("StoragePhysical")]
[TestCategory("WalPressure")]
public sealed class SqliteWalPressureTests
{
    private const long ReducedThreshold = 256 * 1024;
    public TestContext TestContext { get; set; } = null!;

    [TestMethod]
    public async Task PinnedReader_StopsFurtherWritesAcrossReopen_WithoutGrowingOnRejectedRetries()
    {
        var time = new CheckpointTimeProvider();
        await using var fixture = await SqliteStorageFileFixture.CreateAsync(ReducedThreshold, time);
        await InsertRowAsync(fixture.Anchor, "Devices", Guid.NewGuid());
        await SqliteStorageMeasurement.CheckpointAsync(fixture.Factory, truncate: true);
        await using var reader = fixture.Factory.CreateConnection();
        await reader.OpenAsync();
        using (var snapshot = reader.BeginTransaction(deferred: true))
        {
            await PinAsync(reader, snapshot);
            await FillUntilPressureAsync(fixture.Anchor);
            var baseline = await SqliteStorageMeasurement.CaptureAsync(fixture.Factory, fixture.DatabasePath);
            baseline.WriteTo(TestContext, "wal-pressure-rejected");
            Assert.AreEqual(ReducedThreshold, baseline.WalPressureLimitBytes);
            Assert.IsTrue(baseline.WalBytes >= ReducedThreshold);
            var previous = await ScalarAsync(fixture.Anchor, "SELECT InvalidSyncAttemptCount FROM Devices;");
            for (var retry = 0; retry < 32; retry++)
            {
                await using var reopened = fixture.Factory.CreateConnection();
                await reopened.OpenAsync();
                var error = await Assert.ThrowsExactlyAsync<SqliteException>(() =>
                    ExecuteAsync(reopened, "UPDATE Devices SET InvalidSyncAttemptCount = 999999;"));
                Assert.IsTrue(SqliteCapacityError.IsCapacityFailure(error));
                StringAssert.Contains(error.Message, "wal-pressure");
                Assert.AreEqual(previous, await ScalarAsync(reopened, "SELECT InvalidSyncAttemptCount FROM Devices;"));
                Assert.AreEqual(baseline.WalBytes, new FileInfo(fixture.DatabasePath + "-wal").Length,
                    $"Rejected retry {retry} appended WAL bytes.");
            }
            var after = await SqliteStorageMeasurement.CaptureAsync(fixture.Factory, fixture.DatabasePath);
            baseline.AssertSameRows(after);
            CollectionAssert.AreEqual(baseline.ChargedBytes.Values.ToArray(), after.ChargedBytes.Values.ToArray());
            // A new factory has no process-local pressure cache. The file-backed guard must still reject.
            var restarted = new SqliteConnectionFactory(fixture.Anchor.ConnectionString, ReducedThreshold, time);
            await using var restartedConnection = restarted.CreateConnection();
            await restartedConnection.OpenAsync();
            Assert.IsTrue(SqliteCapacityError.IsCapacityFailure(await Assert.ThrowsExactlyAsync<SqliteException>(() =>
                ExecuteAsync(restartedConnection, "DELETE FROM Devices;"))));
        }
        // Reader release does not cause a tight retry loop: the same factory observes its cooldown.
        await using (var immediateRetry = fixture.Factory.CreateConnection())
        {
            await immediateRetry.OpenAsync();
            Assert.AreEqual(0L, await ScalarAsync(immediateRetry, "SELECT pml_wal_admit();"));
            Assert.IsTrue(SqliteCapacityError.IsCapacityFailure(await Assert.ThrowsExactlyAsync<SqliteException>(() =>
                ExecuteAsync(immediateRetry, "UPDATE Devices SET InvalidSyncAttemptCount = 123456;"))));
        }
        // A normal retry after the reader gap invokes SQLite maintenance; no live file is deleted.
        time.Advance(StorageCapacityConstants.WalPressureRetryInterval);
        await using var resumed = fixture.Factory.CreateConnection();
        await resumed.OpenAsync();
        await ExecuteAsync(resumed, "UPDATE Devices SET InvalidSyncAttemptCount = 123456;");
        Assert.AreEqual(123456L, await ScalarAsync(resumed, "SELECT InvalidSyncAttemptCount FROM Devices;"));
        Assert.IsTrue(new FileInfo(fixture.DatabasePath + "-wal").Length < ReducedThreshold);
    }

    [TestMethod]
    public async Task AllTableInsertPaths_AndLedgerWrites_EncounterThePressureGuard()
    {
        var time = new CheckpointTimeProvider();
        await using var fixture = await SqliteStorageFileFixture.CreateAsync(ReducedThreshold, time);
        await InsertRowAsync(fixture.Anchor, "Devices", Guid.NewGuid());
        await ExecuteAsync(fixture.Anchor, "PRAGMA foreign_keys = OFF;");
        await SqliteStorageMeasurement.CheckpointAsync(fixture.Factory, truncate: true);
        await using var reader = fixture.Factory.CreateConnection();
        await reader.OpenAsync();
        using var snapshot = reader.BeginTransaction(deferred: true);
        await PinAsync(reader, snapshot);
        await FillUntilPressureAsync(fixture.Anchor);
        foreach (var policy in SqliteStoragePolicy.Tables)
        {
            var failure = await Assert.ThrowsExactlyAsync<SqliteException>(() =>
                InsertRowAsync(fixture.Anchor, policy.TableName, Guid.NewGuid()));
            Assert.IsTrue(SqliteCapacityError.IsCapacityFailure(failure), policy.TableName);
            StringAssert.Contains(failure.Message, "wal-pressure", policy.TableName);
        }
        foreach (var sql in new[] { "UPDATE StorageUsage SET UsedBytes = UsedBytes;",
                     "UPDATE StorageBudgets SET UsedBytes = UsedBytes;" })
            StringAssert.Contains((await Assert.ThrowsExactlyAsync<SqliteException>(() =>
                ExecuteAsync(fixture.Anchor, sql))).Message, "wal-pressure");

        var clock = new SyncVersionClockStore(fixture.Factory);
        Assert.ThrowsExactly<StorageCapacityExceededException>(() => clock.TryWrite(new SyncVersionClockState
        {
            LastPhysicalTimeUnixMilliseconds = 1, LastLogicalCounter = 0,
            LastUpdatedAtUtc = DateTimeOffset.UnixEpoch, Version = 1
        }, expectedVersion: null));
        Assert.AreEqual(0L, await ScalarAsync(fixture.Anchor, "SELECT COUNT(*) FROM SyncVersionClockState;"));
        Assert.AreEqual(0L, await ScalarAsync(fixture.Anchor, "SELECT COUNT(*) FROM CriticalControlIngress;"));
    }

    [TestMethod]
    public async Task AlreadyOpenSession_RejectsWithoutNotification_AndRecoversOnOrdinaryRetry()
    {
        var time = new CheckpointTimeProvider();
        await using var fixture = await SqliteStorageFileFixture.CreateAsync(ReducedThreshold, time);
        await InsertRowAsync(fixture.Anchor, "Devices", Guid.NewGuid());
        using var hub = new BackendChangeEventHub(TimeProvider.System);
        using var session = new SqliteSession(fixture.Factory, hub);
        using (var open = await session.CreateCommandAsync("SELECT COUNT(*) FROM Devices;"))
            Assert.AreEqual(1L, Convert.ToInt64(await open.ExecuteScalarAsync()));
        await SqliteStorageMeasurement.CheckpointAsync(fixture.Factory, truncate: true);
        await using var reader = fixture.Factory.CreateConnection();
        await reader.OpenAsync();
        using (var snapshot = reader.BeginTransaction(deferred: true))
        {
            await PinAsync(reader, snapshot);
            await FillUntilPressureAsync(fixture.Anchor);
            SqliteStorageBudgetTests.StageWork(session);
            await Assert.ThrowsExactlyAsync<StorageCapacityExceededException>(() => session.SaveChangesAsync());
            Assert.AreEqual(0L, hub.CurrentPosition.Sequence);
            Assert.AreEqual(0L, await ScalarAsync(fixture.Anchor, "SELECT COUNT(*) FROM SyncItems;"));
            using var read = await session.CreateCommandAsync("SELECT COUNT(*) FROM Devices;");
            Assert.AreEqual(1L, Convert.ToInt64(await read.ExecuteScalarAsync()), "Pressure must preserve read availability.");
        }
        time.Advance(StorageCapacityConstants.WalPressureRetryInterval);
        Assert.AreEqual(1, await session.SaveChangesAsync());
        Assert.AreEqual(1L, hub.CurrentPosition.Sequence);
        Assert.AreEqual(1L, await ScalarAsync(fixture.Anchor, "SELECT COUNT(*) FROM SyncItems;"));
    }

    [TestMethod]
    public async Task SpillingTransaction_StopsWithinTheTransaction_AndOuterRollbackPreservesTheLedger()
    {
        const long threshold = 64 * 1024;
        var time = new CheckpointTimeProvider();
        await using var fixture = await SqliteStorageFileFixture.CreateAsync(threshold, time);
        using var hub = new BackendChangeEventHub(TimeProvider.System);
        using var session = new SqliteSession(fixture.Factory, hub);
        await using (var transaction = await session.BeginTransactionAsync())
        {
            using (var cache = await session.CreateCommandAsync("PRAGMA cache_size = 2; PRAGMA cache_spill = ON;"))
                await cache.ExecuteNonQueryAsync();
            var rejected = false;
            for (var index = 0; index < 1024; index++)
            {
                SqliteStorageBudgetTests.StageWork(session);
                try { await session.SaveChangesAsync(); }
                catch (StorageCapacityExceededException)
                {
                    rejected = true;
                    break;
                }
            }
            Assert.IsTrue(rejected, "Forced cache spills must reach the reduced physical pressure threshold within one transaction.");
            var highWater = new FileInfo(fixture.DatabasePath + "-wal").Length;
            Assert.IsTrue(highWater >= threshold);
            for (var retry = 0; retry < 16; retry++)
            {
                await Assert.ThrowsExactlyAsync<StorageCapacityExceededException>(() => session.SaveChangesAsync());
                Assert.AreEqual(highWater, new FileInfo(fixture.DatabasePath + "-wal").Length);
            }
            session.ClearTrackedChanges();
            await transaction.RollbackAsync();
        }
        Assert.AreEqual(0L, hub.CurrentPosition.Sequence);
        Assert.AreEqual(0L, await ScalarAsync(fixture.Anchor, "SELECT COUNT(*) FROM SyncItems;"));
        Assert.AreEqual(0L, await ScalarAsync(fixture.Anchor, "SELECT SUM(UsedBytes) FROM StorageBudgets;"));
        time.Advance(StorageCapacityConstants.WalPressureRetryInterval);
        await using var reopened = fixture.Factory.CreateConnection();
        await reopened.OpenAsync();
        await InsertRowAsync(reopened, "SyncItems", Guid.NewGuid());
        Assert.AreEqual(1L, await ScalarAsync(reopened, "SELECT COUNT(*) FROM SyncItems;"));
    }

    [TestMethod]
    public async Task MissingGuard_IsRejectedAtStartup_AndAnUnconfiguredHandleCannotBypassIt()
    {
        await using var fixture = await SqliteStorageFileFixture.CreateAsync();
        await InsertRowAsync(fixture.Anchor, "Devices", Guid.NewGuid());
        await using (var unconfigured = new SqliteConnection(fixture.Anchor.ConnectionString))
        {
            await unconfigured.OpenAsync();
            var failure = await Assert.ThrowsExactlyAsync<SqliteException>(() =>
                ExecuteAsync(unconfigured, "UPDATE Devices SET InvalidSyncAttemptCount = 42;"));
            StringAssert.Contains(failure.Message, SqliteWalPressure.FunctionName);
            Assert.AreEqual(1L, await ScalarAsync(unconfigured, "SELECT COUNT(*) FROM Devices;"));
        }
        await ExecuteAsync(fixture.Anchor, "DROP TRIGGER WalPressure_Devices_UPDATE;");
        await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => AppDatabaseInitializer.InitializeAsync(fixture.Anchor));
        Assert.AreEqual(1L, await ScalarAsync(fixture.Anchor, "SELECT InvalidSyncAttemptCount FROM Devices;"));
    }

    [TestMethod]
    public async Task EscapedFileUri_AndPooledReopen_ReinstallTheAdmissionFunction()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"pml-wal-uri-ő #%-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, "vault #%.db");
        var time = new CheckpointTimeProvider();
        var factory = new SqliteConnectionFactory(DatabaseEncryptionMigrator.CreateRuntimeConnectionString(path, "test-key"),
            ReducedThreshold, time);
        try
        {
            SQLitePCL.Batteries_V2.Init();
            await using var connection = factory.CreateConnection();
            await connection.OpenAsync();
            await AppDatabaseInitializer.InitializeAsync(connection);
            await SqliteStorageMeasurement.CheckpointAsync(factory, truncate: true);
            Assert.AreEqual(1L, await ScalarAsync(connection, "SELECT pml_wal_admit();"));
            await connection.CloseAsync();
            await connection.OpenAsync();
            Assert.AreEqual(1L, await ScalarAsync(connection, "SELECT pml_wal_admit();"));
            await InsertRowAsync(connection, "Devices", Guid.NewGuid());
            await SqliteStorageMeasurement.CheckpointAsync(factory, truncate: true);
            await using var reader = factory.CreateConnection();
            await reader.OpenAsync();
            using (var snapshot = reader.BeginTransaction(deferred: true))
            {
                await PinAsync(reader, snapshot);
                await FillUntilPressureAsync(connection);
                await connection.CloseAsync();
                await connection.OpenAsync();
                Assert.AreEqual(0L, await ScalarAsync(connection, "SELECT pml_wal_admit();"),
                    "The callback must observe the actual URI-resolved WAL, including after pooled reopen.");
                Assert.IsTrue(SqliteCapacityError.IsCapacityFailure(await Assert.ThrowsExactlyAsync<SqliteException>(() =>
                    ExecuteAsync(connection, "UPDATE Devices SET InvalidSyncAttemptCount = 42;"))));
            }
            time.Advance(StorageCapacityConstants.WalPressureRetryInterval);
            await connection.CloseAsync();
            await connection.OpenAsync();
            Assert.AreEqual(1L, await ScalarAsync(connection, "SELECT pml_wal_admit();"));
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            Directory.Delete(directory, recursive: true);
        }
    }

    private static async Task PinAsync(SqliteConnection connection, SqliteTransaction snapshot)
    {
        using var command = connection.CreateCommand();
        command.Transaction = snapshot;
        command.CommandText = "SELECT COUNT(*) FROM Devices;";
        await command.ExecuteScalarAsync();
    }

    private static async Task FillUntilPressureAsync(SqliteConnection connection)
    {
        using var command = connection.CreateCommand();
        command.CommandText = "UPDATE Devices SET InvalidSyncAttemptCount = $value;";
        var value = command.Parameters.Add("$value", SqliteType.Integer);
        for (var index = 0; index < 1024; index++)
        {
            value.Value = index + 2;
            try { await command.ExecuteNonQueryAsync(); }
            catch (SqliteException exception) when (SqliteCapacityError.IsCapacityFailure(exception))
            {
                StringAssert.Contains(exception.Message, "wal-pressure");
                return;
            }
        }
        Assert.Fail("A pinned reader must exhaust the reduced WAL allowance before 1024 same-size commits.");
    }

    private sealed class CheckpointTimeProvider : TimeProvider
    {
        private long _timestamp;
        public override long TimestampFrequency => TimeSpan.TicksPerSecond;
        public override long GetTimestamp() => Interlocked.Read(ref _timestamp);
        public void Advance(TimeSpan elapsed) => Interlocked.Add(ref _timestamp, elapsed.Ticks);
    }
}
