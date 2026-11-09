using System.Security.Cryptography;
using Microsoft.Data.Sqlite;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using PasswordManagerLocal.Common.Backend.Abstractions.Notifications;
using PasswordManagerLocal.Common.Backend.Abstractions.Persistence;
using PasswordManagerLocal.Common.Backend.Configuration;
using PasswordManagerLocal.Common.Backend.Models;
using PasswordManagerLocal.Common.Backend.Persistence;
using PasswordManagerLocal.Common.Backend.Sync;
using PasswordManagerLocal.Common.Contracts.Notifications;
using PasswordManagerLocal.Common.Tests.TestInfrastructure;
using SQLitePCL;

namespace PasswordManagerLocal.Common.Tests.Backend.Persistence;

[TestClass]
[TestCategory("Backend")]
[TestCategory("Integration")]
public sealed class SqliteSessionTests
{
    static SqliteSessionTests() => Batteries_V2.Init();

    [TestMethod]
    public async Task StandaloneFlush_WhenSecondWriteFails_RollsBackAllRowsAndNotifications()
    {
        using var fixture = await SessionFixture.CreateAsync();
        using var hub = new BackendChangeEventHub(TimeProvider.System);
        using var session = fixture.OpenSession(hub);
        var id = Guid.NewGuid();
        StageInsert(session, id, [1]);
        StageInsert(session, id, [2]);

        var failure = await Assert.ThrowsExactlyAsync<StorageWriteException>(() => session.SaveChangesAsync());

        Assert.AreEqual(StorageWriteException.FailureKind.UniqueKeyConflict, failure.Kind);
        Assert.AreEqual(typeof(TestRow), failure.RowType);
        Assert.IsNull(await ReadValueAsync(session, id));
        Assert.AreEqual(0L, hub.CurrentPosition.Sequence);

        session.ClearTrackedChanges();
        StageInsert(session, id, [3]);
        Assert.AreEqual(1, await session.SaveChangesAsync());
        CollectionAssert.AreEqual(new byte[] { 3 }, await ReadValueAsync(session, id));
        Assert.AreEqual(1L, hub.CurrentPosition.Sequence);

        session.ClearTrackedChanges();
        CollectionAssert.AreEqual(new byte[] { 3 }, await ReadValueAsync(session, id));
        Assert.AreEqual(1L, hub.CurrentPosition.Sequence);
    }

    [TestMethod]
    public async Task FailedSavepoint_CanRetryWithoutLosingEarlierFlushAndPublishesAfterCommit()
    {
        using var fixture = await SessionFixture.CreateAsync();
        using var hub = new BackendChangeEventHub(TimeProvider.System);
        using var session = fixture.OpenSession(hub);
        using var reader = fixture.OpenSession();
        var id = Guid.NewGuid();
        var failedId = Guid.NewGuid();
        await using var subscription = hub.Subscribe(id, capacity: 4);
        await using var transaction = await session.BeginTransactionAsync();
        Assert.IsTrue(AppDatabaseTransactionRegistry.HasActiveTransaction(fixture.DatabaseKey));

        StageInsert(session, id, [1]);
        Assert.IsNull(await ReadValueAsync(session, id));
        Assert.AreEqual(1, await session.SaveChangesAsync());
        CollectionAssert.AreEqual(new byte[] { 1 }, await ReadValueAsync(session, id));
        Assert.IsNull(await ReadValueAsync(reader, id));

        StageInsert(session, failedId, [2]);
        StageInsert(session, id, [3]);
        await Assert.ThrowsExactlyAsync<StorageWriteException>(() => session.SaveChangesAsync());
        Assert.IsNull(await ReadValueAsync(session, failedId));
        CollectionAssert.AreEqual(new byte[] { 1 }, await ReadValueAsync(session, id));
        session.ClearTrackedChanges();

        StageUpdate(session, id, [1], [4]);
        Assert.AreEqual(1, await session.SaveChangesAsync());
        Assert.AreEqual(0L, hub.CurrentPosition.Sequence);
        await transaction.CommitAsync();

        Assert.IsFalse(AppDatabaseTransactionRegistry.HasActiveTransaction(fixture.DatabaseKey));
        CollectionAssert.AreEqual(new byte[] { 4 }, await ReadValueAsync(reader, id));
        Assert.AreEqual(1L, hub.CurrentPosition.Sequence);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await using var deliveries = subscription.ReadAllAsync(timeout.Token).GetAsyncEnumerator();
        Assert.IsTrue(await deliveries.MoveNextAsync());
        var notification = deliveries.Current.Notification!;
        Assert.AreEqual(FrontendChangeScope.UserProfile | FrontendChangeScope.Passwords, notification.Scope);
        Assert.AreEqual(id, notification.UserId);
    }

    [TestMethod]
    public async Task CompetingSessions_CompareOriginalBlobAndReportStaleRow()
    {
        using var fixture = await SessionFixture.CreateAsync();
        using var first = fixture.OpenSession();
        using var second = fixture.OpenSession();
        var id = Guid.NewGuid();
        StageInsert(first, id, [1]);
        await first.SaveChangesAsync();
        var original = (await ReadValueAsync(first, id))!;

        StageUpdate(first, id, original, [2]);
        StageUpdate(second, id, original, [3]);
        CryptographicOperations.ZeroMemory(original);
        await first.SaveChangesAsync();

        var failure = await Assert.ThrowsExactlyAsync<StorageWriteException>(() => second.SaveChangesAsync());
        Assert.AreEqual(StorageWriteException.FailureKind.StaleRow, failure.Kind);
        Assert.AreEqual(typeof(TestRow), failure.RowType);
        CollectionAssert.AreEqual(new byte[] { 2 }, await ReadValueAsync(second, id));
    }

    [TestMethod]
    [Timeout(10_000)]
    public async Task BusyWriter_IsReportedAndPendingRowsCanBeRetried()
    {
        using var fixture = await SessionFixture.CreateAsync();
        using var first = fixture.OpenSession();
        using var second = fixture.OpenSession();
        var id = Guid.NewGuid();
        await using var transaction = await first.BeginTransactionAsync();
        StageInsert(second, id, [1]);

        var failure = await Assert.ThrowsExactlyAsync<StorageWriteException>(() => second.SaveChangesAsync());
        Assert.AreEqual(StorageWriteException.FailureKind.Busy, failure.Kind);

        await transaction.RollbackAsync();
        Assert.IsFalse(AppDatabaseTransactionRegistry.HasActiveTransaction(fixture.DatabaseKey));
        Assert.AreEqual(1, await second.SaveChangesAsync());
    }

    [TestMethod]
    public async Task CanceledCommitAndDisposal_RollBackFlushedRowsAndDiscardPendingChanges()
    {
        using var fixture = await SessionFixture.CreateAsync();
        using var hub = new BackendChangeEventHub(TimeProvider.System);
        using var session = fixture.OpenSession(hub);
        using var reader = fixture.OpenSession();
        var id = Guid.NewGuid();
        var stagedId = Guid.NewGuid();
        var canceled = new CancellationToken(canceled: true);

        await using (var transaction = await session.BeginTransactionAsync())
        {
            StageInsert(session, id, [1]);
            await session.SaveChangesAsync();
            StageInsert(session, stagedId, [2]);
            await Assert.ThrowsExactlyAsync<OperationCanceledException>(() => transaction.CommitAsync(canceled));
            Assert.IsTrue(AppDatabaseTransactionRegistry.HasActiveTransaction(fixture.DatabaseKey));
        }

        Assert.IsFalse(AppDatabaseTransactionRegistry.HasActiveTransaction(fixture.DatabaseKey));
        Assert.AreEqual(0, await session.SaveChangesAsync());
        Assert.IsNull(await ReadValueAsync(reader, id));
        Assert.IsNull(await ReadValueAsync(reader, stagedId));
        Assert.AreEqual(0L, hub.CurrentPosition.Sequence);
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task AccountWrites_RejectBothPendingAndFlushedDeletionBarriers(bool flushBarrier)
    {
        using var fixture = await SessionFixture.CreateAsync();
        using var session = fixture.OpenSession();
        var userId = Guid.NewGuid();
        await using var transaction = await session.BeginTransactionAsync();
        session.Stage<DeletedUserBarrier>(SqliteMutation.Operation.Insert, """
            INSERT INTO "DeletedUserBarriers" (
                "UserId", "AppliedAtUtc", "DeletedAtUtc", "DeletionGeneration", "DeletionOperationId",
                "HasConflict", "KeyEpoch", "LastUpdatedAtUtc", "MembershipEpoch", "OperationHash",
                "OriginDeviceId", "OriginInstanceId", "OriginSequence", "OriginSignPublicKey", "OriginSignature")
            VALUES ($id, $now, $now, $id, $id, 0, 1, $now, 1, X'00', $id, $id, 1, X'00', X'00');
            """, userId, null, ("$id", userId), ("$now", DateTimeOffset.UtcNow));
        if (flushBarrier)
            await session.SaveChangesAsync();

        session.Stage<UserLoginIdentityState>(SqliteMutation.Operation.Update, """
            UPDATE "UserLoginIdentityStates" SET "ConcurrencyVersion" = 2 WHERE "UserId" = $id;
            """, userId, null, ("$id", userId));
        var failure = await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => session.SaveChangesAsync());
        StringAssert.Contains(failure.Message, "account-deletion barrier");
    }

    [TestMethod]
    public async Task ExplicitChangedFields_SuppressHeartbeatAndRetainAuthenticationInvalidation()
    {
        using var fixture = await SessionFixture.CreateAsync();
        using var hub = new BackendChangeEventHub(TimeProvider.System);
        using var session = fixture.OpenSession(hub);
        var id = Guid.NewGuid();
        StageInsert(session, id, [1]);
        await session.SaveChangesAsync();

        var heartbeat = new BackendChangeSet();
        heartbeat.AddEntityMutation(new Device(), addedOrDeleted: false, name => name == nameof(Device.LastSeen));
        session.Stage<TestRow>(SqliteMutation.Operation.Update,
            """UPDATE "SessionRows" SET "Value" = X'02' WHERE "Id" = $id;""",
            null, heartbeat, ("$id", id));
        await session.SaveChangesAsync();
        Assert.AreEqual(1L, hub.CurrentPosition.Sequence);

        var loginChange = new BackendChangeSet();
        loginChange.AddEntityMutation(new UserLoginIdentityState { UserId = id }, addedOrDeleted: false,
            name => name == nameof(UserLoginIdentityState.UsernameHash));
        await using var subscription = hub.Subscribe(id, capacity: 4);
        session.Stage<TestRow>(SqliteMutation.Operation.Update,
            """UPDATE "SessionRows" SET "Value" = X'03' WHERE "Id" = $id;""",
            null, loginChange, ("$id", id));
        await session.SaveChangesAsync();

        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await using var deliveries = subscription.ReadAllAsync(timeout.Token).GetAsyncEnumerator();
        Assert.IsTrue(await deliveries.MoveNextAsync());
        var notification = deliveries.Current.Notification!;
        Assert.AreEqual(FrontendChangeScope.UserProfile | FrontendChangeScope.AuthenticationSessions, notification.Scope);
        Assert.AreEqual(id, notification.UserId);
    }

    private static void StageInsert(SqliteSession session, Guid id, byte[] value)
    {
        var changes = new BackendChangeSet();
        changes.Add(id, FrontendChangeScope.UserProfile);
        session.Stage<TestRow>(SqliteMutation.Operation.Insert,
            """INSERT INTO "SessionRows" ("Id", "Value") VALUES ($id, $value);""",
            null, changes, ("$id", id), ("$value", value));
    }

    private static void StageUpdate(SqliteSession session, Guid id, byte[] original, byte[] value)
    {
        var changes = new BackendChangeSet();
        changes.Add(id, FrontendChangeScope.Passwords);
        session.Stage<TestRow>(SqliteMutation.Operation.Update,
            """UPDATE "SessionRows" SET "Value" = $value WHERE "Id" = $id AND "Value" = $original;""",
            null, changes, ("$id", id), ("$original", original), ("$value", value));
    }

    private static async Task<byte[]?> ReadValueAsync(SqliteSession session, Guid id)
    {
        using var command = await session.CreateCommandAsync("""SELECT "Value" FROM "SessionRows" WHERE "Id" = $id;""");
        command.Parameters.AddWithValue("$id", id);
        return (byte[]?)await command.ExecuteScalarAsync();
    }

    private sealed class TestRow { }

    private sealed class SessionFixture(string directory, SqliteConnectionFactory connections, string databaseKey)
        : IDisposable
    {
        public string DatabaseKey { get; } = databaseKey;

        public static async Task<SessionFixture> CreateAsync()
        {
            var directory = Path.Combine(Path.GetTempPath(), "PasswordManagerLocal.SessionTests", Guid.NewGuid().ToString("N"));
            try
            {
                var paths = new BackendStoragePaths(directory);
                using var encrypted = new SqliteConnectionFactory(paths, new TestKeyProtector()).CreateConnection();
                var options = new SqliteConnectionStringBuilder(encrypted.ConnectionString)
                {
                    Pooling = false,
                    DefaultTimeout = 1
                };
                var connections = new SqliteConnectionFactory(options.ToString());
                await using var connection = connections.CreateConnection();
                await AppDatabaseInitializer.InitializeAsync(connection);
                await connection.OpenAsync();
                using var command = connection.CreateCommand();
                command.CommandText = """CREATE TABLE "SessionRows" ("Id" TEXT PRIMARY KEY NOT NULL, "Value" BLOB NOT NULL);""";
                await command.ExecuteNonQueryAsync();
                return new SessionFixture(directory, connections, AppDatabaseTransactionRegistry.GetDatabaseKey(connection));
            }
            catch
            {
                if (Directory.Exists(directory))
                    Directory.Delete(directory, recursive: true);
                throw;
            }
        }

        public SqliteSession OpenSession(IBackendChangePublisher? publisher = null) => new(connections, publisher);

        public void Dispose() => Directory.Delete(directory, recursive: true);
    }
}
