using Microsoft.Data.Sqlite;
using PasswordManagerLocal.Common.Backend.Persistence;
using PasswordManagerLocal.Common.Backend.Services;
using PasswordManagerLocal.Common.Tests.Fakes;
using SQLitePCL;

namespace PasswordManagerLocal.Common.Tests.TestInfrastructure.Services.Fixtures;

internal sealed class ClockFixture : IAsyncDisposable
{
    private readonly SqliteConnection _keepAlive;

    private ClockFixture(SqliteConnection keepAlive, SyncVersionClockStore store, DateTimeOffset utcNow)
    {
        _keepAlive = keepAlive;
        Store = store;
        Time = new ManualTimeProvider { UtcNow = utcNow };
        Clock = new SyncVersionClockService(Store, Identity, Time);
    }

    public FakeDeviceIdentityService Identity { get; } = new();
    public ManualTimeProvider Time { get; }
    public SyncVersionClockStore Store { get; }
    public SyncVersionClockService Clock { get; }

    public static async Task<ClockFixture> CreateAsync(DateTimeOffset utcNow)
    {
        Batteries_V2.Init();
        var connections = new SqliteConnectionFactory(
            $"Data Source=Clock-{Guid.NewGuid():N};Mode=Memory;Cache=Shared;Pooling=False");
        var keepAlive = connections.CreateConnection();
        try
        {
            await keepAlive.OpenAsync();
            await AppDatabaseInitializer.InitializeAsync(keepAlive);
            return new ClockFixture(keepAlive, new SyncVersionClockStore(connections), utcNow);
        }
        catch
        {
            await keepAlive.DisposeAsync();
            throw;
        }
    }

    public ValueTask DisposeAsync() => _keepAlive.DisposeAsync();
}
