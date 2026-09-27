using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using PasswordManagerLocal.Common.Contracts.Endpoints;
using PasswordManagerLocal.Common.Backend.Abstractions.Persistence;
using PasswordManagerLocal.Common.Backend.Abstractions.Repositories;
using PasswordManagerLocal.Common.Backend.Abstractions.Security;
using PasswordManagerLocal.Common.Backend.Abstractions.Services;
using PasswordManagerLocal.Common.Backend.Abstractions.State;
using PasswordManagerLocal.Common.Backend.Abstractions.Sync.Discovery;
using PasswordManagerLocal.Common.Backend.Abstractions.Sync.Presence;
using PasswordManagerLocal.Common.Backend.Configuration;
using PasswordManagerLocal.Common.Backend.Internal.Devices;
using PasswordManagerLocal.Common.Backend.Persistence;
using PasswordManagerLocal.Common.Backend.Repositories;
using PasswordManagerLocal.Common.Backend.Security;
using PasswordManagerLocal.Common.Backend.Services;
using PasswordManagerLocal.Common.Backend.Services.Discovery;
using PasswordManagerLocal.Common.Backend.Services.Hosted;
using PasswordManagerLocal.Common.Backend.State;
using PasswordManagerLocal.Common.Backend.Sync.Discovery;
using PasswordManagerLocal.Common.Backend.Sync.Presence;
using PasswordManagerLocal.Common.Backend.Sync.Tcp;
using PasswordManagerLocal.Common.Backend.Utils;

namespace PasswordManagerLocal.Common.Backend.DependencyInjection;

public static class BackendServiceCollectionExtensions
{
    public static IServiceCollection AddPasswordManagerLocalBackend(
        this IServiceCollection services,
        BackendStoragePaths storagePaths,
        IKeyProtector keyProtector,
        ILocalDiscoveryNetworkLease discoveryNetworkLease,
        IBackendExecutionProfileProvider executionProfileProvider)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.AddPasswordManagerLocalBackendCore(
            storagePaths,
            keyProtector,
            discoveryNetworkLease,
            executionProfileProvider);
        services.AddPasswordManagerLocalSynchronization();
        services.AddPasswordManagerLocalInteractiveServices();
        return services;
    }

    public static IServiceCollection AddPasswordManagerLocalBackendCore(
        this IServiceCollection services,
        BackendStoragePaths storagePaths,
        IKeyProtector keyProtector,
        ILocalDiscoveryNetworkLease discoveryNetworkLease,
        IBackendExecutionProfileProvider executionProfileProvider)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(storagePaths);
        ArgumentNullException.ThrowIfNull(keyProtector);
        ArgumentNullException.ThrowIfNull(discoveryNetworkLease);
        ArgumentNullException.ThrowIfNull(executionProfileProvider);

        services.AddSingleton(storagePaths);
        services.AddSingleton<IBackendExecutionProfileProvider>(executionProfileProvider);
        services.AddSingleton<IDeviceEnrollmentAvailability>(executionProfileProvider);
        services.AddSingleton<IKeyProtector>(keyProtector);
        services.AddSingleton<ILocalDiscoveryNetworkLease>(discoveryNetworkLease);
        services.AddSingleton<IBackendInitializationService, BackendInitializationService>();
        services.AddSingleton<RelationshipIntegrityMaterializationInterceptor>();

        services.AddDbContextPool<AppDbContext>((sp, opts) =>
        {
            var paths = sp.GetRequiredService<BackendStoragePaths>();
            var protector = sp.GetRequiredService<IKeyProtector>();
            var dbPassword = DbConfigManager.GetOrCreateSqlCipherPassword(paths, protector);

            var connStr = new SqliteConnectionStringBuilder
            {
                DataSource = paths.DatabasePath,
                Password = dbPassword
            }.ToString();

            opts.UseSqlite(connStr);
            opts.AddInterceptors(sp.GetRequiredService<RelationshipIntegrityMaterializationInterceptor>());
        });

        services.AddScoped<IUnitOfWork, AppUnitOfWork>();
        services.AddScoped<IUserRepository, UserRepository>();
        services.AddScoped<IDeviceRepository, DeviceRepository>();
        services.AddScoped<IUserDeviceRepository, UserDeviceRepository>();
        services.AddScoped<ILocalUserDeviceRepository, LocalUserDeviceRepository>();
        services.AddScoped<ISyncRouteRepository, SyncRouteRepository>();
        services.AddScoped<IGroupRepository, GroupRepository>();
        services.AddScoped<ISyncQueueRepository, SyncQueueRepository>();
        services.AddScoped<ISyncItemRepository, SyncItemRepository>();
        services.AddScoped<ISyncTombstoneRepository, SyncTombstoneRepository>();
        services.AddScoped<IDeviceIdentityRepository, DeviceIdentityRepository>();
        services.AddScoped<IUserSyncSnapshotRepository, UserSyncSnapshotRepository>();
        services.AddScoped<IUserSyncStateRepository, UserSyncStateRepository>();
        services.AddScoped<IUserRevisionKnowledgeRepository, UserRevisionKnowledgeRepository>();
        services.AddScoped<IUserControlOperationRepository, UserControlOperationRepository>();
        services.AddScoped<IUserControlStateRepository, UserControlStateRepository>();
        services.AddScoped<IUserMembershipAuthorizationRepository, UserMembershipAuthorizationRepository>();
        services.AddScoped<IUserOriginRemovalCutoffRepository, UserOriginRemovalCutoffRepository>();
        services.AddScoped<IDeviceEnrollmentCommitRepository, DeviceEnrollmentCommitRepository>();
        services.AddScoped<IDeletedUserBarrierRepository, DeletedUserBarrierRepository>();
        services.AddScoped<IUserCanonicalCheckpointRepository, UserCanonicalCheckpointRepository>();
        services.AddScoped<IUserSyncFaultRepository, UserSyncFaultRepository>();

        services.AddScoped<IUserDataBundleIntegrityService, UserDataBundleIntegrityService>();
        services.AddScoped<IUserDataBundleVerificationService, UserDataBundleVerificationService>();
        services.AddScoped<IUserSnapshotBatchVerificationService, UserSnapshotBatchVerificationService>();
        services.AddScoped<IUserSyncFaultService, UserSyncFaultService>();
        services.AddScoped<IUserCanonicalHealthService, UserCanonicalHealthService>();
        services.AddScoped<IDatabaseHealthService, DatabaseHealthService>();

        services.AddSingleton<IUserLifecycleCoordinator, UserLifecycleCoordinator>();
        services.AddSingleton<IInteractiveSessionStateService, InteractiveSessionStateService>();
        services.AddSingleton<IInteractiveUserDataStateAccessor, InteractiveUserDataStateAccessor>();
        services.AddSingleton(TimeProvider.System);

        services.AddSingleton<UserDataRecoveryScheduler>();
        services.AddSingleton<IUserDataRecoveryScheduler>(sp => sp.GetRequiredService<UserDataRecoveryScheduler>());
        services.AddSingleton<UserDataRecoveryHostedService>();
        services.AddSingleton<IBackendHostedService>(sp => sp.GetRequiredService<UserDataRecoveryHostedService>());
        services.AddSingleton<LocalDeviceCleanupHostedService>();
        services.AddSingleton<IBackendHostedService>(sp => sp.GetRequiredService<LocalDeviceCleanupHostedService>());
        services.AddSingleton<PendingUserControlOperationRecoveryHostedService>();
        services.AddSingleton<IBackendHostedService>(sp => sp.GetRequiredService<PendingUserControlOperationRecoveryHostedService>());

        return services;
    }

    public static IServiceCollection AddPasswordManagerLocalSynchronization(
        this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.AddSingleton<ISyncVersionClockService, SyncVersionClockService>();
        services.AddSingleton<IDeviceIdentityService, DeviceIdentityService>();
        services.AddSingleton<ISyncDeviceIdentityService, SyncDeviceIdentityService>();
        services.AddSingleton<IDiscoveredDeviceEndpointRegistry, DiscoveredDeviceEndpointRegistry>();
        services.AddSingleton<IDevicePresenceRegistry, DevicePresenceRegistry>();
        services.AddSingleton<ISyncTransportClientService, TcpSyncClientService>();
        services.AddSingleton<IDevicePresenceProbeService, DevicePresenceProbeService>();
        services.AddSingleton<DeviceOnlineStatusEvaluator>();
        services.AddSingleton<IDeviceSyncTaskService, DeviceSyncTaskService>();
        services.AddSingleton<IEnrollmentRuntimeState, EnrollmentRuntimeState>();
        services.AddSingleton<ILocalNetworkAddressService, LocalNetworkAddressService>();
        services.AddSingleton<ILocalDiscoveryTransport, UdpLocalDiscoveryTransport>();
        services.AddSingleton<LocalDiscoveryHostedService>();
        services.AddSingleton<ILocalDiscoveryService>(sp => sp.GetRequiredService<LocalDiscoveryHostedService>());
        services.AddSingleton<ISyncRuntimeService, SyncRuntimeService>();
        services.AddSingleton<IDeviceEnrollmentEndpointService, DeviceEnrollmentEndpointService>();
        services.AddSingleton<IDeviceEnrollmentLocalLinkService, DeviceEnrollmentLocalLinkService>();
        services.AddSingleton<IDeviceEnrollmentRegistrationService, DeviceEnrollmentRegistrationService>();
        services.AddSingleton<IDeviceEnrollmentSnapshotService, DeviceEnrollmentSnapshotService>();
        services.AddSingleton<IDeviceEnrollmentSnapshotTransferService, DeviceEnrollmentSnapshotTransferService>();
        services.AddSingleton<IDeviceEnrollmentSnapshotImporterService, DeviceEnrollmentSnapshotImporterService>();
        services.AddSingleton<DeviceEnrollmentService>();
        services.AddSingleton<IDeviceEnrollmentService>(sp => sp.GetRequiredService<DeviceEnrollmentService>());
        services.AddSingleton<IDeviceEnrollmentLifecycleCoordinator>(sp => sp.GetRequiredService<DeviceEnrollmentService>());

        services.AddScoped<IUserLoginIdentityProjectionService, UserLoginIdentityProjectionService>();
        services.AddScoped<IUserLookupService, UserLookupService>();
        services.AddScoped<IUserDataReaderService, UserDataReaderService>();
        services.AddScoped<IUserDataPersistenceValidator, UserDataPersistenceValidator>();
        services.AddScoped<IUserDataWriterService, UserDataWriterService>();
        services.AddScoped<IUserAccountDeletionCleanupService, UserAccountDeletionCleanupService>();
        services.AddScoped<IUserPasswordsDataMergeService, UserPasswordsDataMergeService>();
        services.AddScoped<IUserDevicesDataMergeService, UserDevicesDataMergeService>();
        services.AddScoped<ISyncRelationshipReconciliationService, SyncRelationshipReconciliationService>();
        services.AddScoped<IUserDataBundleSyncService, UserDataBundleSyncService>();
        services.AddScoped<IUserDataRecoveryCoordinator, UserDataRecoveryCoordinator>();
        services.AddScoped<IUserSnapshotPublisherService, UserSnapshotPublisherService>();
        services.AddScoped<IUserSnapshotInboxService, UserSnapshotInboxService>();
        services.AddScoped<IUserSnapshotMergeCoordinator, UserSnapshotMergeCoordinator>();
        services.AddScoped<IUserSnapshotAntiEntropyService, UserSnapshotAntiEntropyService>();
        services.AddScoped<IUserMembershipAuthorizationService, UserMembershipAuthorizationService>();
        services.AddScoped<IUserControlOperationWriterService, UserControlOperationWriterService>();
        services.AddScoped<IUserControlOperationInboxService, UserControlOperationInboxService>();
        services.AddScoped<IUserControlOperationAntiEntropyService, UserControlOperationAntiEntropyService>();
        services.AddScoped<IUserSyncKeyResolverService, UserSyncKeyResolverService>();
        services.AddScoped<IUserTombstoneGarbageCollector, UserTombstoneGarbageCollector>();
        services.AddScoped<IUserDeltaApplierService, UserDeltaApplierService>();
        services.AddScoped<INetworkDeltaProtocolService, NetworkDeltaProtocolService>();
        services.AddScoped<INetworkDeltaReplayService, NetworkDeltaReplayService>();
        services.AddScoped<INetworkDeltaPayloadApplierService, NetworkDeltaPayloadApplierService>();
        services.AddScoped<INetworkDeltaLifecycleService, NetworkDeltaLifecycleService>();
        services.AddScoped<IOutgoingDeltaBuilderService, OutgoingDeltaBuilderService>();
        services.AddScoped<INetworkDeltaService, NetworkDeltaService>();
        services.AddScoped<IIncomingDeltaApplierService, IncomingDeltaApplierService>();
        services.AddScoped<ISyncAuthorizationService, SyncAuthorizationService>();
        services.AddScoped<IDeviceSecurityService, DeviceSecurityService>();
        services.AddScoped<ILocalDeviceMatcherService, LocalDeviceMatcherService>();
        services.AddScoped<ISyncItemLifecycleService, SyncItemLifecycleService>();
        services.AddScoped<ISyncTargetResolverService, SyncTargetResolverService>();
        services.AddScoped<IPendingSyncActivationService, PendingSyncActivationService>();
        services.AddScoped<ISyncQueueWriterService, SyncQueueWriterService>();
        services.AddScoped<ISyncChangeQueueService, SyncChangeQueueService>();
        services.AddScoped<IUserSyncCatchUpService, UserSyncCatchUpService>();
        services.AddScoped<ISyncQueueService, SyncQueueService>();
        services.AddScoped<ISyncService, SyncService>();

        services.AddSingleton<SyncPeerProtocolHandler>();
        services.AddSingleton<SyncDeviceIdentityWarmupHostedService>();
        services.AddSingleton<TcpSyncServerHostedService>();
        services.AddSingleton<SyncNetworkRefreshHostedService>();
        services.AddSingleton<ISyncControlledHostedService>(sp => sp.GetRequiredService<SyncDeviceIdentityWarmupHostedService>());
        services.AddSingleton<ISyncControlledHostedService>(sp => sp.GetRequiredService<TcpSyncServerHostedService>());
        services.AddSingleton<ISyncControlledHostedService>(sp => sp.GetRequiredService<LocalDiscoveryHostedService>());
        services.AddSingleton<ISyncControlledHostedService>(sp => sp.GetRequiredService<SyncNetworkRefreshHostedService>());

        return services;
    }

    public static IServiceCollection AddPasswordManagerLocalInteractiveServices(
        this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.AddTransient<IEndpoints, Endpoints>();
        services.AddScoped<IUserPasswordsService, UserPasswordsService>();
        services.AddScoped<IUserCustomColorService, UserCustomColorService>();
        services.AddScoped<IUserPasswordTagService, UserPasswordTagService>();
        services.AddScoped<IPasswordService, PasswordService>();
        services.AddScoped<ICustomUserColorService, CustomUserColorService>();
        services.AddScoped<IPasswordTagService, PasswordTagService>();
        services.AddScoped<IGroupService, GroupService>();
        services.AddScoped<IGroupPasswordsService, GroupPasswordsService>();
        services.AddScoped<IUserRecoverySessionService, UserRecoverySessionService>();
        services.AddScoped<IUserRegistrationService, UserRegistrationService>();
        services.AddScoped<IUserLoginService, UserLoginService>();
        services.AddScoped<AuthSessionService>();
        services.AddScoped<IAuthSessionService>(sp => sp.GetRequiredService<AuthSessionService>());
        services.AddScoped<IAuthenticatedSessionIssuer>(sp => sp.GetRequiredService<AuthSessionService>());
        services.AddScoped<ICredentialVerificationService, CredentialVerificationService>();
        services.AddScoped<IMasterPasswordRotationService, MasterPasswordRotationService>();
        services.AddScoped<IAuthService, AuthService>();
        services.AddScoped<IUserSessionService, UserSessionService>();
        services.AddScoped<IUserDeletionService, UserDeletionService>();
        services.AddScoped<IUserService, UserService>();
        services.AddScoped<IRememberMeService, RememberMeService>();
        services.AddScoped<IUserProfileService, UserProfileService>();
        services.AddScoped<LocalUserDeviceLinkManager>();
        services.AddScoped<UserDeviceAccessor>();
        services.AddScoped<UserDeviceMetadataEditor>();
        services.AddScoped<ILocalDeviceSettingsService, LocalDeviceSettingsService>();
        services.AddScoped<IUserDeviceQueryService, UserDeviceQueryService>();
        services.AddScoped<IUserDeviceSettingsService, UserDeviceSettingsService>();
        services.AddScoped<IUserDeviceDisconnectionService, UserDeviceDisconnectionService>();
        services.AddScoped<IDeviceService, DeviceService>();

        services.AddMemoryCache();
        services.AddSingleton<SafeMemoryCache>();
        services.AddSingleton<ITokenService, TokenService>();
        services.AddSingleton<IKeyVaultService, KeyVaultService>();
        services.AddSingleton<IDataCachingService, DataCachingService>();
        services.AddSingleton<IInteractiveSensitiveStateResetter, InteractiveSensitiveStateResetter>();

        services.AddSingleton<DevicePresencePollingHostedService>();
        services.AddSingleton<IInteractiveBackendHostedService>(sp => sp.GetRequiredService<DevicePresencePollingHostedService>());
        services.AddSingleton<ExpiredEntriesPurgeHostedService>();
        services.AddSingleton<IInteractiveBackendHostedService>(sp => sp.GetRequiredService<ExpiredEntriesPurgeHostedService>());

        return services;
    }
}
