using Microsoft.Extensions.Caching.Memory;
using NSec.Cryptography;
using PasswordManagerLocal.Common.Backend;
using PasswordManagerLocal.Common.Contracts.Endpoints;
using PasswordManagerLocal.Common.Backend.Abstractions.Sync.Discovery;
using PasswordManagerLocal.Common.Backend.Abstractions.Sync.Presence;
using PasswordManagerLocal.Common.Backend.Abstractions.Persistence;
using PasswordManagerLocal.Common.Backend.Abstractions.Repositories;
using PasswordManagerLocal.Common.Backend.Abstractions.Security;
using PasswordManagerLocal.Common.Backend.Abstractions.Services;
using PasswordManagerLocal.Common.Contracts.Requests;
using PasswordManagerLocal.Common.Backend.Security;
using PasswordManagerLocal.Common.Backend.Services;
using PasswordManagerLocal.Common.Backend.Sync.Discovery;
using PasswordManagerLocal.Common.Backend.Sync.Presence;
using PasswordManagerLocal.Common.Backend.Internal.Devices;
using PasswordManagerLocal.Common.Tests.Fakes;
using System.Text;

namespace PasswordManagerLocal.Common.Tests.TestInfrastructure;

public sealed class BackendTestHost : IDisposable
{
    private readonly ServiceProvider _sp;
    private readonly Key _signingKey;

    public BackendTestHost(
        bool useRealSnapshotMergeCoordinator = false,
        IKeyProtector? keyProtector = null)
    {
        var sc = new ServiceCollection();
        _signingKey = Key.Create(SignatureAlgorithm.Ed25519, new KeyCreationParameters());

        sc.AddSingleton<IMemoryCache>(_ => new MemoryCache(new MemoryCacheOptions { SizeLimit = 100_000 }));
        sc.AddSingleton<SafeMemoryCache>();

        sc.AddSingleton<ITokenService, TokenService>();
        sc.AddSingleton<IKeyVaultService, KeyVaultService>();
        sc.AddSingleton<ISyncVersionClockService>(sp =>
            new EphemeralSyncVersionClockService(sp.GetRequiredService<IDeviceIdentityService>()));

        sc.AddSingleton<IDataCachingService>(sp =>
        {
            var cache = sp.GetRequiredService<SafeMemoryCache>();
            var tokens = sp.GetRequiredService<ITokenService>();
            return new DataCachingService(cache, tokens);
        });

        sc.AddSingleton<IKeyProtector>(keyProtector ?? new TestKeyProtector());
        sc.AddSingleton<IEndpoints, Endpoints>();

        sc.AddSingleton<IUserRepository, InMemoryUserRepository>();
        sc.AddSingleton<IGroupRepository, FakeGroupRepository>();
        sc.AddSingleton<FakeUserDeviceRepository>();
        sc.AddSingleton<IUserDeviceRepository>(sp => sp.GetRequiredService<FakeUserDeviceRepository>());
        sc.AddSingleton<IDeviceRepository, FakeDeviceRepository>();
        sc.AddSingleton<ISyncQueueRepository, FakeSyncQueueRepository>();
        sc.AddSingleton<FakeLocalUserDeviceRepository>();
        sc.AddSingleton<ILocalUserDeviceRepository>(sp => sp.GetRequiredService<FakeLocalUserDeviceRepository>());
        sc.AddSingleton<ISyncRouteRepository, FakeSyncRouteRepository>();
        sc.AddSingleton<IDeviceIdentityService>(new FakeDeviceIdentityService
        {
            AgreementPublicKey = Enumerable.Repeat((byte)0xA5, 32).ToArray(),
            SignPublicKey = _signingKey.PublicKey.Export(KeyBlobFormat.RawPublicKey),
            FingerprintHex = Convert.ToHexString(Enumerable.Repeat((byte)0x5A, 32).ToArray()),
            SignHandler = bytes => SignatureAlgorithm.Ed25519.Sign(_signingKey, bytes)
        });
        sc.AddSingleton<FakeSyncQueueService>();
        sc.AddSingleton<ISyncQueueService>(sp => sp.GetRequiredService<FakeSyncQueueService>());
        sc.AddSingleton<ISyncChangeQueueService>(sp => sp.GetRequiredService<FakeSyncQueueService>());
        sc.AddSingleton<IUserSyncCatchUpService>(sp => sp.GetRequiredService<FakeSyncQueueService>());
        sc.AddSingleton<IPendingSyncActivationService>(sp => sp.GetRequiredService<FakeSyncQueueService>());
        sc.AddSingleton<ISyncRuntimeService, FakeSyncRuntimeService>();
        sc.AddSingleton<ISyncDeviceIdentityService, FakeSyncDeviceIdentityService>();
        sc.AddSingleton<IDiscoveredDeviceEndpointRegistry, DiscoveredDeviceEndpointRegistry>();
        sc.AddSingleton(TimeProvider.System);
        sc.AddSingleton<IDevicePresenceRegistry, DevicePresenceRegistry>();
        sc.AddSingleton<FakeSyncTransportClientService>();
        sc.AddSingleton<ISyncTransportClientService>(sp => sp.GetRequiredService<FakeSyncTransportClientService>());
        var executionProfileProvider = new FakeBackendExecutionProfileProvider();
        executionProfileProvider.SetProfile(
            new BackendExecutionProfile(
                TimeSpan.FromSeconds(15),
                TimeSpan.FromSeconds(15),
                TimeSpan.FromSeconds(35)),
            isInteractive: true);
        sc.AddSingleton<IBackendExecutionProfileProvider>(executionProfileProvider);
        sc.AddSingleton<IDeviceEnrollmentAvailability>(executionProfileProvider);
        sc.AddSingleton<IDevicePresenceProbeService, DevicePresenceProbeService>();
        sc.AddSingleton<DeviceOnlineStatusEvaluator>();
        sc.AddSingleton<IUnitOfWork, FakeUnitOfWork>();
        sc.AddSingleton<IUserSyncSnapshotRepository, FakeUserSyncSnapshotRepository>();
        sc.AddSingleton<IUserSyncStateRepository, FakeUserSyncStateRepository>();
        sc.AddSingleton<IUserRevisionKnowledgeRepository, FakeUserRevisionKnowledgeRepository>();
        sc.AddSingleton<IUserControlOperationRepository, FakeUserControlOperationRepository>();
        sc.AddSingleton<IUserControlStateRepository, FakeUserControlStateRepository>();
        sc.AddSingleton<FakeUserMembershipAuthorizationRepository>();
        sc.AddSingleton<IUserMembershipAuthorizationRepository>(sp => sp.GetRequiredService<FakeUserMembershipAuthorizationRepository>());
        sc.AddSingleton<IUserOriginRemovalCutoffRepository, FakeUserOriginRemovalCutoffRepository>();
        sc.AddSingleton<IDeviceEnrollmentCommitRepository, FakeDeviceEnrollmentCommitRepository>();
        sc.AddSingleton<IDeletedUserBarrierRepository, FakeDeletedUserBarrierRepository>();
        sc.AddSingleton<IUserMembershipAuthorizationService, UserMembershipAuthorizationService>();
        sc.AddSingleton<IUserSyncKeyResolverService, UserSyncKeyResolverService>();
        sc.AddSingleton<IUserLifecycleCoordinator, UserLifecycleCoordinator>();
        sc.AddSingleton<IInteractiveSessionStateService, InteractiveSessionStateService>();
        sc.AddSingleton<IInteractiveUserDataStateAccessor, InteractiveUserDataStateAccessor>();
        sc.AddSingleton<FakeUserDataRecoveryCoordinator>();
        sc.AddSingleton<IUserDataRecoveryCoordinator>(sp => sp.GetRequiredService<FakeUserDataRecoveryCoordinator>());
        sc.AddSingleton<IUserControlOperationWriterService, FakeUserControlOperationWriterService>();
        sc.AddSingleton<IUserSnapshotPublisherService, FakeUserSnapshotPublisherService>();
        sc.AddSingleton<ISyncQueueWriterService, FakeSyncQueueWriterService>();
        sc.AddSingleton<IUserSnapshotInboxService, UserSnapshotInboxService>();
        if (useRealSnapshotMergeCoordinator)
        {
            sc.AddSingleton<IUserPasswordsDataMergeService, UserPasswordsDataMergeService>();
            sc.AddSingleton<IUserDevicesDataMergeService, UserDevicesDataMergeService>();
            sc.AddSingleton<IUserDataBundleVerificationService, UserDataBundleVerificationService>();
            sc.AddSingleton<IUserSnapshotBatchVerificationService, UserSnapshotBatchVerificationService>();
            sc.AddSingleton<IUserDataBundleSyncService, UserDataBundleSyncService>();
            sc.AddSingleton<IUserSnapshotMergeCoordinator, UserSnapshotMergeCoordinator>();
        }
        else
        {
            sc.AddSingleton<IUserSnapshotMergeCoordinator, FakeUserSnapshotMergeCoordinator>();
        }

        sc.AddSingleton<IUserDataBundleIntegrityService, UserDataBundleIntegrityService>();
        sc.AddSingleton<IUserSessionService, UserSessionService>();
        sc.AddSingleton<IUserRecoverySessionService, UserRecoverySessionService>();
        sc.AddSingleton<IUserLoginIdentityProjectionService, UserLoginIdentityProjectionService>();
        sc.AddSingleton<IUserLookupService, UserLookupService>();
        sc.AddSingleton<IUserDataReaderService, UserDataReaderService>();
        sc.AddSingleton<IUserDataPersistenceValidator, UserDataPersistenceValidator>();
        sc.AddSingleton<IUserDataWriterService, UserDataWriterService>();
        sc.AddSingleton<IUserTombstoneGarbageCollector, UserTombstoneGarbageCollector>();
        sc.AddSingleton<IUserAccountDeletionCleanupService, FakeUserAccountDeletionCleanupService>();
        sc.AddSingleton<IUserDeletionService, UserDeletionService>();
        sc.AddSingleton<IUserService, UserService>();
        sc.AddSingleton<IUserProfileService, UserProfileService>();
        sc.AddSingleton<IRememberMeService, RememberMeService>();
        sc.AddSingleton<IUserRegistrationService, UserRegistrationService>();
        sc.AddSingleton<IUserLoginService, UserLoginService>();
        sc.AddSingleton<AuthSessionService>();
        sc.AddSingleton<IAuthSessionService>(sp => sp.GetRequiredService<AuthSessionService>());
        sc.AddSingleton<IAuthenticatedSessionIssuer>(sp => sp.GetRequiredService<AuthSessionService>());
        sc.AddSingleton<ICredentialVerificationService, CredentialVerificationService>();
        sc.AddSingleton<IMasterPasswordRotationService, MasterPasswordRotationService>();
        sc.AddSingleton<IAuthService, AuthService>();
        sc.AddSingleton<IPasswordService, PasswordService>();
        sc.AddSingleton<ICustomUserColorService, CustomUserColorService>();
        sc.AddSingleton<IPasswordTagService, PasswordTagService>();
        sc.AddSingleton<IUserPasswordsService, UserPasswordsService>();
        sc.AddSingleton<IUserCustomColorService, UserCustomColorService>();
        sc.AddSingleton<IUserPasswordTagService, UserPasswordTagService>();
        sc.AddSingleton<LocalUserDeviceLinkManager>();
        sc.AddSingleton<UserDeviceAccessor>();
        sc.AddSingleton<UserDeviceMetadataEditor>();
        sc.AddSingleton<ILocalDeviceSettingsService, LocalDeviceSettingsService>();
        sc.AddSingleton<IUserDeviceQueryService, UserDeviceQueryService>();
        sc.AddSingleton<IUserDeviceSettingsService, UserDeviceSettingsService>();
        sc.AddSingleton<IUserDeviceDisconnectionService, UserDeviceDisconnectionService>();
        sc.AddSingleton<IDeviceService, DeviceService>();

        _sp = sc.BuildServiceProvider(new ServiceProviderOptions
        {
            ValidateOnBuild = true,
            ValidateScopes = true
        });
        _sp.GetRequiredService<IInteractiveSessionStateService>()
            .ActivateAsync()
            .GetAwaiter()
            .GetResult();
    }

    public IServiceProvider Services => _sp;

    public RegistrationRequest CreateValidRegistrationRequest(string username = "testuser") =>
        new RegistrationRequest
        {
            Username = username,
            Password = Encoding.UTF8.GetBytes("P@ssw0rd12345678"),
            FirstName = "Test",
            LastName = "User",
            Email = "test@example.com",
            RememberMe = false
        };


    public LoginRequest CreateValidLoginRequest(string username = "testuser", bool rememberMe = false) =>
        new LoginRequest
        {
            Username = username,
            Password = Encoding.UTF8.GetBytes("P@ssw0rd12345678"),
            RememberMe = rememberMe
        };


    public void Dispose()
    {
        _sp.Dispose();
        _signingKey.Dispose();
    }
}