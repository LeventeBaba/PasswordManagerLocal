using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using PasswordManagerLocal.Common.Backend.Abstractions.Persistence;
using PasswordManagerLocal.Common.Backend.Abstractions.Repositories;
using PasswordManagerLocal.Common.Backend.Persistence;
using PasswordManagerLocal.Common.Backend.Repositories;

namespace PasswordManagerLocal.Common.Tests.TestInfrastructure;

internal static class BackendStorageTestRegistration
{
    // Share the stateless interceptor so equivalent DbContext options can reuse EF's internal provider.
    private static readonly RelationshipIntegrityMaterializationInterceptor IntegrityInterceptor = new();

    public static void UseEfStorageBaseline(this IServiceCollection services)
    {
        services.AddDbContextPool<AppDbContext>((sp, options) =>
        {
            options.UseSqlite(sp.GetRequiredService<SqliteConnectionFactory>().ConnectionString);
            options.AddInterceptors(IntegrityInterceptor);
        });
        services.AddScoped<AppUnitOfWork>();
        services.Replace(ServiceDescriptor.Scoped<IUnitOfWork>(sp => sp.GetRequiredService<AppUnitOfWork>()));
        services.Replace(ServiceDescriptor.Scoped<IUserAccountDeletionStore, EfUserAccountDeletionStore>());
        services.Replace(ServiceDescriptor.Scoped<IUserRecoveryJournalStore, EfUserRecoveryJournalStore>());
        services.Replace(ServiceDescriptor.Scoped<IUserRepository, UserRepository>());
        services.Replace(ServiceDescriptor.Scoped<IDeviceRepository, DeviceRepository>());
        services.Replace(ServiceDescriptor.Scoped<IUserDeviceRepository, UserDeviceRepository>());
        services.Replace(ServiceDescriptor.Scoped<ILocalUserDeviceRepository, LocalUserDeviceRepository>());
        services.Replace(ServiceDescriptor.Scoped<ISyncRouteRepository, SyncRouteRepository>());
        services.Replace(ServiceDescriptor.Scoped<IGroupRepository, GroupRepository>());
        services.Replace(ServiceDescriptor.Scoped<ISyncQueueRepository, SyncQueueRepository>());
        services.Replace(ServiceDescriptor.Scoped<ISyncItemRepository, SyncItemRepository>());
        services.Replace(ServiceDescriptor.Scoped<ISyncTombstoneRepository, SyncTombstoneRepository>());
        services.Replace(ServiceDescriptor.Scoped<IDeviceIdentityRepository, DeviceIdentityRepository>());
        services.Replace(ServiceDescriptor.Scoped<IUserSyncSnapshotRepository, UserSyncSnapshotRepository>());
        services.Replace(ServiceDescriptor.Scoped<IUserSyncStateRepository, UserSyncStateRepository>());
        services.Replace(ServiceDescriptor.Scoped<IUserRevisionKnowledgeRepository, UserRevisionKnowledgeRepository>());
        services.Replace(ServiceDescriptor.Scoped<IUserControlOperationRepository, UserControlOperationRepository>());
        services.Replace(ServiceDescriptor.Scoped<IUserControlStateRepository, UserControlStateRepository>());
        services.Replace(ServiceDescriptor.Scoped<IUserMembershipAuthorizationRepository, UserMembershipAuthorizationRepository>());
        services.Replace(ServiceDescriptor.Scoped<IUserOriginRemovalCutoffRepository, UserOriginRemovalCutoffRepository>());
        services.Replace(ServiceDescriptor.Scoped<IDeviceEnrollmentCommitRepository, DeviceEnrollmentCommitRepository>());
        services.Replace(ServiceDescriptor.Scoped<IDeletedUserBarrierRepository, DeletedUserBarrierRepository>());
        services.Replace(ServiceDescriptor.Scoped<IUserCanonicalCheckpointRepository, UserCanonicalCheckpointRepository>());
        services.Replace(ServiceDescriptor.Scoped<IUserSyncFaultRepository, UserSyncFaultRepository>());
    }
}
