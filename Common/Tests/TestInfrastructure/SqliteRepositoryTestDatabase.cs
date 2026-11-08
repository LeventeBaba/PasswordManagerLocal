using PasswordManagerLocal.Common.Backend.Models;
using PasswordManagerLocal.Common.Backend.Persistence;
using PasswordManagerLocal.Common.Backend.Repositories.Sqlite;

namespace PasswordManagerLocal.Common.Tests.TestInfrastructure;

/// <summary>Production repositories sharing a single SQLite session on an isolated test vault.</summary>
internal sealed class SqliteRepositoryTestDatabase : IAsyncDisposable
{
    private readonly SqliteTestVault _vault;

    private SqliteRepositoryTestDatabase(SqliteTestVault vault)
    {
        _vault = vault;
        UnitOfWork = OpenSession();
        DeviceIdentity = new SqliteDeviceIdentityRepository(UnitOfWork);
        Users = new SqliteUserRepository(UnitOfWork);
        Groups = new SqliteGroupRepository(UnitOfWork);
        Devices = new SqliteDeviceRepository(UnitOfWork);
        UserDevices = new SqliteUserDeviceRepository(UnitOfWork);
        LocalUserDevices = new SqliteLocalUserDeviceRepository(UnitOfWork);
        SyncRoutes = new SqliteSyncRouteRepository(UnitOfWork);
        SyncItems = new SqliteSyncItemRepository(UnitOfWork);
        SyncQueue = new SqliteSyncQueueRepository(UnitOfWork);
        Tombstones = new SqliteSyncTombstoneRepository(UnitOfWork);
        UserRevisionKnowledge = new SqliteUserRevisionKnowledgeRepository(UnitOfWork);
        UserControlStates = new SqliteUserControlStateRepository(UnitOfWork);
        UserMembershipAuthorizations = new SqliteUserMembershipAuthorizationRepository(UnitOfWork);
        DeviceEnrollmentCommits = new SqliteDeviceEnrollmentCommitRepository(UnitOfWork);
        DeletedUserBarriers = new SqliteDeletedUserBarrierRepository(UnitOfWork);
        UserCanonicalCheckpoints = new SqliteUserCanonicalCheckpointRepository(UnitOfWork);
        UserSyncFaults = new SqliteUserSyncFaultRepository(UnitOfWork);
    }

    public SqliteSession UnitOfWork { get; }
    public SqliteDeviceIdentityRepository DeviceIdentity { get; }
    public SqliteUserRepository Users { get; }
    public SqliteGroupRepository Groups { get; }
    public SqliteDeviceRepository Devices { get; }
    public SqliteUserDeviceRepository UserDevices { get; }
    public SqliteLocalUserDeviceRepository LocalUserDevices { get; }
    public SqliteSyncRouteRepository SyncRoutes { get; }
    public SqliteSyncItemRepository SyncItems { get; }
    public SqliteSyncQueueRepository SyncQueue { get; }
    public SqliteSyncTombstoneRepository Tombstones { get; }
    public SqliteUserRevisionKnowledgeRepository UserRevisionKnowledge { get; }
    public SqliteUserControlStateRepository UserControlStates { get; }
    public SqliteUserMembershipAuthorizationRepository UserMembershipAuthorizations { get; }
    public SqliteDeviceEnrollmentCommitRepository DeviceEnrollmentCommits { get; }
    public SqliteDeletedUserBarrierRepository DeletedUserBarriers { get; }
    public SqliteUserCanonicalCheckpointRepository UserCanonicalCheckpoints { get; }
    public SqliteUserSyncFaultRepository UserSyncFaults { get; }

    public static async Task<SqliteRepositoryTestDatabase> CreateAsync() => new(await SqliteTestVault.CreateAsync());

    public SqliteSession OpenSession() => new(_vault.Connections);

    // Inspect persisted rows directly, including processed rows the sender excludes.
    public Task<List<SyncQueueItem>> ReadQueueRowsAsync() =>
        UnitOfWork.ReadAsync("""
            SELECT "QueueId", "Id", "DeviceId", "SyncItemId", "EnqueuedAt", "ProcessedAt"
            FROM "SyncQueueItems" ORDER BY "QueueId";
            """, row => new SyncQueueItem
        {
            QueueId = row.GetInt64(0),
            Id = row.GetGuid(1),
            DeviceId = row.GetGuid(2),
            SyncItemId = row.GetGuid(3),
            EnqueuedAt = row.UtcOffset(4),
            ProcessedAt = row.NullableUtcOffset(5)
        }, default);

    public async ValueTask DisposeAsync()
    {
        try
        {
            UnitOfWork.Dispose();
        }
        finally
        {
            await _vault.DisposeAsync();
        }
    }
}
