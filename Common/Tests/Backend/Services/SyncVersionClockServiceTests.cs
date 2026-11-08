using Microsoft.Data.Sqlite;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using PasswordManagerLocal.Common.Backend.Models;
using PasswordManagerLocal.Common.Backend.Models.Encrypted;
using PasswordManagerLocal.Common.Backend.Persistence;
using PasswordManagerLocal.Common.Backend.Repositories.Sqlite;
using PasswordManagerLocal.Common.Backend.Services;
using PasswordManagerLocal.Common.Tests.Fakes;
using SQLitePCL;

using MSTestAssert = Microsoft.VisualStudio.TestTools.UnitTesting.Assert;

namespace PasswordManagerLocal.Common.Tests.Backend.Services;

[TestClass]
public sealed class SyncVersionClockServiceTests
{
    [TestMethod]
    public async Task Next_SameMillisecondConcurrentAndRestart_RemainsUniqueAndMonotonic()
    {
        await using var fixture = await ClockFixture.CreateAsync(DateTimeOffset.FromUnixTimeMilliseconds(10_000));
        var first = fixture.Clock.Next();
        var generated = await Task.WhenAll(Enumerable.Range(0, 32).Select(_ => Task.Run(fixture.Clock.Next)));
        var restarted = new SyncVersionClockService(fixture.Store, fixture.Identity, fixture.Time);
        var afterRestart = restarted.Next();

        MSTestAssert.AreEqual(33, new[] { first }.Concat(generated).Select(Format).Distinct().Count());
        MSTestAssert.IsTrue(generated.All(stamp => stamp.PhysicalTimeUnixMilliseconds == first.PhysicalTimeUnixMilliseconds));
        MSTestAssert.IsTrue(generated.Max(stamp => stamp.LogicalCounter) > first.LogicalCounter);
        MSTestAssert.IsTrue(afterRestart.LogicalCounter > generated.Max(stamp => stamp.LogicalCounter));
    }

    [TestMethod]
    public async Task Next_WhenClockMovesBack_DoesNotDecrease()
    {
        await using var fixture = await ClockFixture.CreateAsync(DateTimeOffset.FromUnixTimeMilliseconds(20_000));
        var first = fixture.Clock.Next();
        fixture.Time.UtcNow = DateTimeOffset.FromUnixTimeMilliseconds(1_000);
        var second = fixture.Clock.Next();

        MSTestAssert.AreEqual(first.PhysicalTimeUnixMilliseconds, second.PhysicalTimeUnixMilliseconds);
        MSTestAssert.IsTrue(second.LogicalCounter > first.LogicalCounter);
    }

    [TestMethod]
    public async Task Observe_RemoteMaximum_AdvancesSubsequentLocalMutation()
    {
        await using var fixture = await ClockFixture.CreateAsync(DateTimeOffset.FromUnixTimeMilliseconds(10_000));
        fixture.Clock.Observe([
            new()
            {
                PhysicalTimeUnixMilliseconds = 50_000,
                LogicalCounter = 7,
                OriginDeviceId = Guid.NewGuid(),
                OriginInstanceId = Guid.NewGuid()
            }
        ]);

        var next = fixture.Clock.Next();
        MSTestAssert.AreEqual(50_000, next.PhysicalTimeUnixMilliseconds);
        MSTestAssert.AreEqual(8, next.LogicalCounter);
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    [Timeout(10_000)]
    public async Task Next_CompetingWrite_ReloadsSingletonBeforeIssuing(bool existingRow)
    {
        await using var fixture = await ClockFixture.CreateAsync(DateTimeOffset.FromUnixTimeMilliseconds(10_000));
        if (existingRow)
            _ = fixture.Clock.Next();

        var expectedVersion = fixture.Store.Read()?.Version;
        var competingState = new SyncVersionClockState
        {
            LastPhysicalTimeUnixMilliseconds = 50_000,
            LastLogicalCounter = 7,
            LastUpdatedAtUtc = fixture.Time.UtcNow,
            Version = (expectedVersion ?? 0) + 1
        };
        var clockWrite = new ClockWriteGate(fixture.Store);
        var clock = new SyncVersionClockService(clockWrite, fixture.Identity, fixture.Time);
        clockWrite.Arm();
        var advancing = Task.Run(clock.Next);
        try
        {
            await clockWrite.Entered.Task.WaitAsync(TimeSpan.FromSeconds(2));
            // This connection commits between the clock's read and conditional write.
            MSTestAssert.IsTrue(fixture.Store.TryWrite(competingState, expectedVersion));
        }
        finally
        {
            clockWrite.Release.TrySetResult();
            await advancing.WaitAsync(TimeSpan.FromSeconds(2));
        }

        var issued = await advancing;
        MSTestAssert.AreEqual(competingState.LastPhysicalTimeUnixMilliseconds, issued.PhysicalTimeUnixMilliseconds);
        MSTestAssert.AreEqual(competingState.LastLogicalCounter + 1, issued.LogicalCounter);
        MSTestAssert.AreEqual(competingState.Version + 1, fixture.Store.Read()!.Version);
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    [Timeout(10_000)]
    public async Task Observe_InsideActiveTransaction_DefersEvenWhileNextHoldsTheClockGate(bool useFileUri)
    {
        Batteries_V2.Init();
        var root = Path.Combine(
            Path.GetTempPath(),
            "PasswordManagerLocal.ClockTests",
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var databasePath = Path.Combine(root, "clock state.db");
        var connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = useFileUri ? $"{new Uri(databasePath).AbsoluteUri}?cache=private" : databasePath,
            Pooling = false
        }.ToString();

        // The writer can use a file URI while the clock uses the same vault by path.
        var clockConnections = new SqliteConnectionFactory(
            new SqliteConnectionStringBuilder { DataSource = databasePath, Pooling = false }.ToString());
        var clockWrite = new ClockWriteGate(new SyncVersionClockStore(clockConnections));
        var writerConnections = new SqliteConnectionFactory(connectionString);

        try
        {
            await using (var initialization = clockConnections.CreateConnection())
                await AppDatabaseInitializer.InitializeAsync(initialization);

            var identity = new FakeDeviceIdentityService();
            var time = new ManualTimeProvider
            {
                UtcNow = DateTimeOffset.FromUnixTimeMilliseconds(10_000)
            };
            var clock = new SyncVersionClockService(clockWrite, identity, time);
            _ = clock.Next();

            var observed = new SyncVersionStamp
            {
                PhysicalTimeUnixMilliseconds = 50_000,
                LogicalCounter = 7,
                OriginDeviceId = Guid.NewGuid(),
                OriginInstanceId = Guid.NewGuid()
            };

            using (var session = new SqliteSession(writerConnections))
            {
                using var unopened = writerConnections.CreateConnection();
                var databaseKey = AppDatabaseTransactionRegistry.GetDatabaseKey(unopened);
                MSTestAssert.AreEqual(databaseKey, clockWrite.DatabaseKey);
                await using var transaction = await session.BeginTransactionAsync();
                using (var command = await session.CreateCommandAsync("""
                    SELECT "LastUpdatedAtUtc" FROM "SyncVersionClockState" WHERE "Id" = 1;
                    """))
                {
                    MSTestAssert.AreEqual(databaseKey, AppDatabaseTransactionRegistry.GetDatabaseKey(command.Connection!));
                    using var reader = await command.ExecuteReaderAsync(CancellationToken.None);
                    MSTestAssert.IsTrue(await reader.ReadAsync());
                    MSTestAssert.AreEqual(time.UtcNow, reader.UtcOffset(0));
                    MSTestAssert.IsFalse(await reader.ReadAsync());
                }
                MSTestAssert.IsTrue(AppDatabaseTransactionRegistry.HasActiveTransaction(databaseKey));

                clockWrite.Arm();
                var advancing = Task.Run(clock.Next);
                Task? observing = null;
                try
                {
                    await clockWrite.Entered.Task.WaitAsync(TimeSpan.FromSeconds(2));
                    observing = Task.Run(() => clock.Observe([observed]));
                    await observing.WaitAsync(TimeSpan.FromSeconds(2));
                }
                finally
                {
                    // Unblock both tasks even if the clock regresses to the previous lock order.
                    try
                    {
                        await transaction.RollbackAsync();
                    }
                    finally
                    {
                        clockWrite.Release.TrySetResult();
                    }
                    await advancing.WaitAsync(TimeSpan.FromSeconds(2));
                    if (observing is not null)
                        await observing.WaitAsync(TimeSpan.FromSeconds(2));
                }

                MSTestAssert.IsFalse(AppDatabaseTransactionRegistry.HasActiveTransaction(databaseKey));
            }

            var next = clock.Next();
            MSTestAssert.AreEqual(observed.PhysicalTimeUnixMilliseconds, next.PhysicalTimeUnixMilliseconds);
            MSTestAssert.AreEqual(observed.LogicalCounter + 1, next.LogicalCounter);

            var restarted = new SyncVersionClockService(clockWrite, identity, time);
            var afterRestart = restarted.Next();
            MSTestAssert.IsTrue(
                afterRestart.PhysicalTimeUnixMilliseconds > next.PhysicalTimeUnixMilliseconds ||
                (afterRestart.PhysicalTimeUnixMilliseconds == next.PhysicalTimeUnixMilliseconds &&
                 afterRestart.LogicalCounter > next.LogicalCounter));
        }
        finally
        {
            try
            {
                Directory.Delete(root, recursive: true);
            }
            catch
            {
            }
        }
    }

    private static string Format(SyncVersionStamp stamp) =>
        $"{stamp.PhysicalTimeUnixMilliseconds}:{stamp.LogicalCounter}:{stamp.OriginDeviceId:N}:{stamp.OriginInstanceId:N}";

    private sealed class ClockWriteGate(ISyncVersionClockStore store) : ISyncVersionClockStore
    {
        private int _armed;
        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public string DatabaseKey => store.DatabaseKey;

        public void Arm() => Volatile.Write(ref _armed, 1);

        public SyncVersionClockState? Read(int? timeoutSeconds = null) => store.Read(timeoutSeconds);

        public bool TryWrite(SyncVersionClockState state, long? expectedVersion, int? timeoutSeconds = null)
        {
            if (Interlocked.Exchange(ref _armed, 0) == 1)
            {
                Entered.TrySetResult();
                Release.Task.GetAwaiter().GetResult();
            }
            return store.TryWrite(state, expectedVersion, timeoutSeconds);
        }
    }
}
