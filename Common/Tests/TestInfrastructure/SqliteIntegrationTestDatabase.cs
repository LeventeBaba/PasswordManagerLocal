using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using PasswordManagerLocal.Common.Backend.Persistence;
using PasswordManagerLocal.Common.Backend.Repositories;
using SQLitePCL;

namespace PasswordManagerLocal.Common.Tests.TestInfrastructure;

public sealed class SqliteIntegrationTestDatabase : IAsyncDisposable
{
    private readonly SqliteConnection _connection;

    static SqliteIntegrationTestDatabase() =>
        Batteries_V2.Init();

    private SqliteIntegrationTestDatabase(SqliteConnection connection, AppDbContext db)
    {
        _connection = connection;
        Db = db;
        UnitOfWork = new AppUnitOfWork(db);
        Users = new UserRepository(db);
        Groups = new GroupRepository(db);
        Devices = new DeviceRepository(db);
        UserDevices = new UserDeviceRepository(db);
        LocalUserDevices = new LocalUserDeviceRepository(db);
        SyncRoutes = new SyncRouteRepository(db);
        SyncItems = new SyncItemRepository(db);
        SyncQueue = new SyncQueueRepository(db);
        Tombstones = new SyncTombstoneRepository(db);
        UserSyncSnapshots = new UserSyncSnapshotRepository(db);
        UserSyncStates = new UserSyncStateRepository(db);
        UserRevisionKnowledge = new UserRevisionKnowledgeRepository(db);
        UserControlOperations = new UserControlOperationRepository(db);
        UserControlStates = new UserControlStateRepository(db);
        UserMembershipAuthorizations = new UserMembershipAuthorizationRepository(db);
        UserOriginRemovalCutoffs = new UserOriginRemovalCutoffRepository(db);
        DeviceEnrollmentCommits = new DeviceEnrollmentCommitRepository(db);
        DeletedUserBarriers = new DeletedUserBarrierRepository(db);
        UserCanonicalCheckpoints = new UserCanonicalCheckpointRepository(db);
        UserSyncFaults = new UserSyncFaultRepository(db);
    }

    public AppDbContext Db { get; }
    public AppUnitOfWork UnitOfWork { get; }
    public UserRepository Users { get; }
    public GroupRepository Groups { get; }
    public DeviceRepository Devices { get; }
    public UserDeviceRepository UserDevices { get; }
    public LocalUserDeviceRepository LocalUserDevices { get; }
    public SyncRouteRepository SyncRoutes { get; }
    public SyncItemRepository SyncItems { get; }
    public SyncQueueRepository SyncQueue { get; }
    public SyncTombstoneRepository Tombstones { get; }
    public UserSyncSnapshotRepository UserSyncSnapshots { get; }
    public UserSyncStateRepository UserSyncStates { get; }
    public UserRevisionKnowledgeRepository UserRevisionKnowledge { get; }
    public UserControlOperationRepository UserControlOperations { get; }
    public UserControlStateRepository UserControlStates { get; }
    public UserMembershipAuthorizationRepository UserMembershipAuthorizations { get; }
    public UserOriginRemovalCutoffRepository UserOriginRemovalCutoffs { get; }
    public DeviceEnrollmentCommitRepository DeviceEnrollmentCommits { get; }
    public DeletedUserBarrierRepository DeletedUserBarriers { get; }
    public UserCanonicalCheckpointRepository UserCanonicalCheckpoints { get; }
    public UserSyncFaultRepository UserSyncFaults { get; }

    public static async Task<SqliteIntegrationTestDatabase> CreateAsync()
    {
        var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseSqlite(connection)
            .Options;
        var db = new AppDbContext(options);
        await db.Database.EnsureCreatedAsync();
        return new SqliteIntegrationTestDatabase(connection, db);
    }

    public async ValueTask DisposeAsync()
    {
        await Db.DisposeAsync();
        await _connection.DisposeAsync();
    }
}
