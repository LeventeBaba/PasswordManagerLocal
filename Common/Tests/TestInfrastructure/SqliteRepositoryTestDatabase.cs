using PasswordManagerLocal.Common.Backend.Abstractions.Notifications;
using PasswordManagerLocal.Common.Backend.Abstractions.Services;
using PasswordManagerLocal.Common.Backend.Models;
using PasswordManagerLocal.Common.Backend.Persistence;
using PasswordManagerLocal.Common.Backend.Repositories;
using PasswordManagerLocal.Common.Tests.TestInfrastructure.Storage;

namespace PasswordManagerLocal.Common.Tests.TestInfrastructure;

/// <summary>Production repositories sharing a single SQLite session on an isolated test vault.</summary>
internal sealed class SqliteRepositoryTestDatabase : IAsyncDisposable
{
    private readonly IAsyncDisposable _vault;

    private SqliteRepositoryTestDatabase(SqliteConnectionFactory connections, IAsyncDisposable vault,
        string databasePath, IBackendChangePublisher? publisher, IRevocationDeliveryActivity? activity)
    {
        _vault = vault;
        Connections = connections;
        DatabasePath = databasePath;
        UnitOfWork = new SqliteSession(Connections, publisher, activity);
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
        RevocationReceipts = new RevocationReceiptRepository(UnitOfWork);
        UserControlOperations = new UserControlOperationRepository(UnitOfWork);
        UserControlStates = new UserControlStateRepository(UnitOfWork);
        UserMembershipAuthorizations = new UserMembershipAuthorizationRepository(UnitOfWork);
        UserOriginRemovalCutoffs = new UserOriginRemovalCutoffRepository(UnitOfWork);
        DeviceEnrollmentCommits = new DeviceEnrollmentCommitRepository(UnitOfWork);
        DeletedUserBarriers = new DeletedUserBarrierRepository(UnitOfWork);
        UserCanonicalCheckpoints = new UserCanonicalCheckpointRepository(UnitOfWork);
        UserSyncFaults = new UserSyncFaultRepository(UnitOfWork);
    }

    public SqliteConnectionFactory Connections { get; }
    public string DatabasePath { get; }
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
    public RevocationReceiptRepository RevocationReceipts { get; }
    public UserControlOperationRepository UserControlOperations { get; }
    public UserControlStateRepository UserControlStates { get; }
    public UserMembershipAuthorizationRepository UserMembershipAuthorizations { get; }
    public UserOriginRemovalCutoffRepository UserOriginRemovalCutoffs { get; }
    public DeviceEnrollmentCommitRepository DeviceEnrollmentCommits { get; }
    public DeletedUserBarrierRepository DeletedUserBarriers { get; }
    public UserCanonicalCheckpointRepository UserCanonicalCheckpoints { get; }
    public UserSyncFaultRepository UserSyncFaults { get; }

    public static async Task<SqliteRepositoryTestDatabase> CreateAsync(
        IBackendChangePublisher? publisher = null, IRevocationDeliveryActivity? activity = null)
    {
        var vault = await SqliteTestVault.CreateAsync();
        return new(vault.Connections, vault, string.Empty, publisher, activity);
    }

    public static async Task<SqliteRepositoryTestDatabase> CreateFileBackedAsync(
        IBackendChangePublisher? publisher = null, IRevocationDeliveryActivity? activity = null)
    {
        var vault = await SqliteStorageFileFixture.CreateAsync();
        return new(vault.Factory, vault, vault.DatabasePath, publisher, activity);
    }

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
