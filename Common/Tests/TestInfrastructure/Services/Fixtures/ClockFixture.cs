using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using PasswordManagerLocal.Common.Backend.Abstractions.Services;
using PasswordManagerLocal.Common.Backend.Persistence;
using PasswordManagerLocal.Common.Backend.Services;
using PasswordManagerLocal.Common.Tests.Fakes;

using MSTestAssert = Microsoft.VisualStudio.TestTools.UnitTesting.Assert;

using PasswordManagerLocal.Common.Tests.TestInfrastructure.Services.TestDoubles;

namespace PasswordManagerLocal.Common.Tests.TestInfrastructure.Services.Fixtures;

internal sealed class ClockFixture : IAsyncDisposable
{
    private readonly SqliteConnection _connection;
    private readonly ServiceProvider _provider;

    private ClockFixture(
        SqliteConnection connection,
        ServiceProvider provider,
        FakeDeviceIdentityService identity,
        ManualTimeProvider time,
        SyncVersionClockService clock)
    {
        _connection = connection;
        _provider = provider;
        Identity = identity;
        Time = time;
        Clock = clock;
    }

    public FakeDeviceIdentityService Identity { get; }
    public ManualTimeProvider Time { get; }
    public SyncVersionClockService Clock { get; }
    public IServiceScopeFactory ScopeFactory => _provider.GetRequiredService<IServiceScopeFactory>();

    public static async Task<ClockFixture> CreateAsync(DateTimeOffset utcNow)
    {
        var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var services = new ServiceCollection();
        services.AddDbContext<AppDbContext>(options => options.UseSqlite(connection));
        var provider = services.BuildServiceProvider();
        await using (var scope = provider.CreateAsyncScope())
            await scope.ServiceProvider.GetRequiredService<AppDbContext>().Database.EnsureCreatedAsync();

        var identity = new FakeDeviceIdentityService();
        var time = new ManualTimeProvider { UtcNow = utcNow };
        var clock = new SyncVersionClockService(provider.GetRequiredService<IServiceScopeFactory>(), identity, time);
        return new ClockFixture(connection, provider, identity, time, clock);
    }

    public async ValueTask DisposeAsync()
    {
        await _provider.DisposeAsync();
        await _connection.DisposeAsync();
    }
}
