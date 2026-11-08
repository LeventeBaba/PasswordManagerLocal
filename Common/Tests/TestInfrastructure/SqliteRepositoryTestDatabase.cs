using PasswordManagerLocal.Common.Backend.Abstractions.Notifications;
using PasswordManagerLocal.Common.Backend.Models;
using PasswordManagerLocal.Common.Backend.Persistence;
using PasswordManagerLocal.Common.Backend.Repositories.Sqlite;

namespace PasswordManagerLocal.Common.Tests.TestInfrastructure;

/// <summary>Production repositories sharing a single SQLite session on an isolated test vault.</summary>
internal sealed class SqliteRepositoryTestDatabase : IAsyncDisposable
{
    private readonly SqliteTestVault _vault;

    private SqliteRepositoryTestDatabase(SqliteTestVault vault, IBackendChangePublisher? publisher)
    {
        _vault = vault;
        UnitOfWork = new SqliteSession(Connections, publisher);
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
        UserSyncSnapshots = new SqliteUserSyncSnapshotRepository(UnitOfWork);
        UserSyncStates = new SqliteUserSyncStateRepository(UnitOfWork);
        UserRevisionKnowledge = new SqliteUserRevisionKnowledgeRepository(UnitOfWork);
        UserControlOperations = new SqliteUserControlOperationRepository(UnitOfWork);
        UserControlStates = new SqliteUserControlStateRepository(UnitOfWork);
        UserMembershipAuthorizations = new SqliteUserMembershipAuthorizationRepository(UnitOfWork);
        UserOriginRemovalCutoffs = new SqliteUserOriginRemovalCutoffRepository(UnitOfWork);
        DeviceEnrollmentCommits = new SqliteDeviceEnrollmentCommitRepository(UnitOfWork);
        DeletedUserBarriers = new SqliteDeletedUserBarrierRepository(UnitOfWork);
        UserCanonicalCheckpoints = new SqliteUserCanonicalCheckpointRepository(UnitOfWork);
        UserSyncFaults = new SqliteUserSyncFaultRepository(UnitOfWork);
    }

    public SqliteConnectionFactory Connections => _vault.Connections;
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
    public SqliteUserSyncSnapshotRepository UserSyncSnapshots { get; }
    public SqliteUserSyncStateRepository UserSyncStates { get; }
    public SqliteUserRevisionKnowledgeRepository UserRevisionKnowledge { get; }
    public SqliteUserControlOperationRepository UserControlOperations { get; }
    public SqliteUserControlStateRepository UserControlStates { get; }
    public SqliteUserMembershipAuthorizationRepository UserMembershipAuthorizations { get; }
    public SqliteUserOriginRemovalCutoffRepository UserOriginRemovalCutoffs { get; }
    public SqliteDeviceEnrollmentCommitRepository DeviceEnrollmentCommits { get; }
    public SqliteDeletedUserBarrierRepository DeletedUserBarriers { get; }
    public SqliteUserCanonicalCheckpointRepository UserCanonicalCheckpoints { get; }
    public SqliteUserSyncFaultRepository UserSyncFaults { get; }

    public static async Task<SqliteRepositoryTestDatabase> CreateAsync(IBackendChangePublisher? publisher = null) =>
        new(await SqliteTestVault.CreateAsync(), publisher);

    public SqliteSession OpenSession() => new(Connections);

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
