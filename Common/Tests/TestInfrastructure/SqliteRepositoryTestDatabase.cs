using PasswordManagerLocal.Common.Backend.Abstractions.Notifications;
using PasswordManagerLocal.Common.Backend.Models;
using PasswordManagerLocal.Common.Backend.Persistence;
using PasswordManagerLocal.Common.Backend.Repositories;

namespace PasswordManagerLocal.Common.Tests.TestInfrastructure;

/// <summary>Production repositories sharing a single SQLite session on an isolated test vault.</summary>
internal sealed class SqliteRepositoryTestDatabase : IAsyncDisposable
{
    private readonly SqliteTestVault _vault;

    private SqliteRepositoryTestDatabase(SqliteTestVault vault, IBackendChangePublisher? publisher)
    {
        _vault = vault;
        UnitOfWork = new SqliteSession(Connections, publisher);
        DeviceIdentity = new DeviceIdentityRepository(UnitOfWork);
        Users = new UserRepository(UnitOfWork);
        Devices = new DeviceRepository(UnitOfWork);
        UserDevices = new UserDeviceRepository(UnitOfWork);
        LocalUserDevices = new LocalUserDeviceRepository(UnitOfWork);
        SyncRoutes = new SyncRouteRepository(UnitOfWork);
        SyncItems = new SyncItemRepository(UnitOfWork);
        SyncQueue = new SyncQueueRepository(UnitOfWork);
        Tombstones = new SyncTombstoneRepository(UnitOfWork);
        UserSyncSnapshots = new UserSyncSnapshotRepository(UnitOfWork);
        UserSyncStates = new UserSyncStateRepository(UnitOfWork);
        UserRevisionKnowledge = new UserRevisionKnowledgeRepository(UnitOfWork);
        UserControlOperations = new UserControlOperationRepository(UnitOfWork);
        UserControlStates = new UserControlStateRepository(UnitOfWork);
        UserMembershipAuthorizations = new UserMembershipAuthorizationRepository(UnitOfWork);
        UserOriginRemovalCutoffs = new UserOriginRemovalCutoffRepository(UnitOfWork);
        DeviceEnrollmentCommits = new DeviceEnrollmentCommitRepository(UnitOfWork);
        DeletedUserBarriers = new DeletedUserBarrierRepository(UnitOfWork);
        UserCanonicalCheckpoints = new UserCanonicalCheckpointRepository(UnitOfWork);
        UserSyncFaults = new UserSyncFaultRepository(UnitOfWork);
    }

    public SqliteConnectionFactory Connections => _vault.Connections;
    public SqliteSession UnitOfWork { get; }
    public DeviceIdentityRepository DeviceIdentity { get; }
    public UserRepository Users { get; }
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
