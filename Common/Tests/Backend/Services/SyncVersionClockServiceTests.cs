using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using PasswordManagerLocal.Common.Backend.Abstractions.Persistence;
using PasswordManagerLocal.Common.Backend.Abstractions.Services;
using PasswordManagerLocal.Common.Backend.Persistence;
using PasswordManagerLocal.Common.Backend.Services;
using PasswordManagerLocal.Common.Tests.Fakes;

using MSTestAssert = Microsoft.VisualStudio.TestTools.UnitTesting.Assert;

using PasswordManagerLocal.Common.Tests.TestInfrastructure.Services.TestDoubles;
using PasswordManagerLocal.Common.Tests.TestInfrastructure.Services.Fixtures;
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
        var restarted = new SyncVersionClockService(fixture.ScopeFactory, fixture.Identity, fixture.Time);
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
    [Timeout(10_000)]
    public async Task Observe_InsideActiveUnitOfWorkTransaction_DefersWithoutBlockingAndNextPersistsFloor()
    {
        var root = Path.Combine(
            Path.GetTempPath(),
            "PasswordManagerLocal.ClockTests",
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var databasePath = Path.Combine(root, "clock.db");
        var connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = databasePath
        }.ToString();

        var services = new ServiceCollection();
        services.AddDbContext<AppDbContext>(options => options.UseSqlite(connectionString));
        services.AddScoped<IUnitOfWork, AppUnitOfWork>();
        var provider = services.BuildServiceProvider();

        try
        {
            await using (var initializationScope = provider.CreateAsyncScope())
            {
                await initializationScope.ServiceProvider
                    .GetRequiredService<AppDbContext>()
                    .Database
                    .EnsureCreatedAsync();
            }

            var identity = new FakeDeviceIdentityService();
            var time = new ManualTimeProvider
            {
                UtcNow = DateTimeOffset.FromUnixTimeMilliseconds(10_000)
            };
            var scopeFactory = provider.GetRequiredService<IServiceScopeFactory>();
            var clock = new SyncVersionClockService(scopeFactory, identity, time);
            _ = clock.Next();

            var observed = new PasswordManagerLocal.Common.Backend.Models.Encrypted.SyncVersionStamp
            {
                PhysicalTimeUnixMilliseconds = 50_000,
                LogicalCounter = 7,
                OriginDeviceId = Guid.NewGuid(),
                OriginInstanceId = Guid.NewGuid()
            };

            await using (var lockScope = provider.CreateAsyncScope())
            {
                var db = lockScope.ServiceProvider.GetRequiredService<AppDbContext>();
                var unitOfWork = lockScope.ServiceProvider.GetRequiredService<IUnitOfWork>();
                var databaseKey = AppDatabaseTransactionRegistry.GetDatabaseKey(db);
                await using var transaction = await unitOfWork.BeginTransactionAsync();
                _ = await db.SyncVersionClockStates.AsNoTracking().SingleAsync();
                MSTestAssert.IsTrue(AppDatabaseTransactionRegistry.HasActiveTransaction(databaseKey));

                await Task.Run(() => clock.Observe([observed]))
                    .WaitAsync(TimeSpan.FromSeconds(2));

                await transaction.RollbackAsync();
                MSTestAssert.IsFalse(AppDatabaseTransactionRegistry.HasActiveTransaction(databaseKey));
            }

            var next = clock.Next();
            MSTestAssert.AreEqual(observed.PhysicalTimeUnixMilliseconds, next.PhysicalTimeUnixMilliseconds);
            MSTestAssert.AreEqual(observed.LogicalCounter + 1, next.LogicalCounter);

            var restarted = new SyncVersionClockService(scopeFactory, identity, time);
            var afterRestart = restarted.Next();
            MSTestAssert.IsTrue(
                afterRestart.PhysicalTimeUnixMilliseconds > next.PhysicalTimeUnixMilliseconds ||
                (afterRestart.PhysicalTimeUnixMilliseconds == next.PhysicalTimeUnixMilliseconds &&
                 afterRestart.LogicalCounter > next.LogicalCounter));
        }
        finally
        {
            await provider.DisposeAsync();
            try
            {
                Directory.Delete(root, recursive: true);
            }
            catch
            {
            }
        }
    }

    private static string Format(PasswordManagerLocal.Common.Backend.Models.Encrypted.SyncVersionStamp stamp) =>
        $"{stamp.PhysicalTimeUnixMilliseconds}:{stamp.LogicalCounter}:{stamp.OriginDeviceId:N}:{stamp.OriginInstanceId:N}";


}
