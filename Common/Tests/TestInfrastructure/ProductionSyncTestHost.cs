using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using PasswordManagerLocal.Common.Contracts.Endpoints;
using PasswordManagerLocal.Common.Backend.Abstractions.Persistence;
using PasswordManagerLocal.Common.Backend.Abstractions.Repositories;
using PasswordManagerLocal.Common.Backend.Abstractions.Services;
using PasswordManagerLocal.Common.Backend.Abstractions.Sync.Discovery;
using PasswordManagerLocal.Common.Backend.Abstractions.Sync.Presence;
using PasswordManagerLocal.Common.Backend.Configuration;
using PasswordManagerLocal.Common.Backend.DependencyInjection;
using PasswordManagerLocal.Common.Backend.Models;
using PasswordManagerLocal.Common.Backend.Persistence;
using PasswordManagerLocal.Common.Contracts.Requests;
using PasswordManagerLocal.Common.Backend.Services;
using PasswordManagerLocal.Common.Backend.Sync.Discovery;
using PasswordManagerLocal.Common.Backend.Sync.Presence;
using PasswordManagerLocal.Common.Backend.Sync.Tcp;
using PasswordManagerLocal.Common.Tests.Fakes;
using SQLitePCL;
using System.Text;

namespace PasswordManagerLocal.Common.Tests.TestInfrastructure;

/// <summary>
/// Creates an isolated production-composition backend for two-database regular synchronization
/// tests. SQLite, repositories, snapshots, queues, encryption, protocol handlers, version clocks,
/// and device sync workers are real. Only operating-system TCP/UDP transport is replaced.
/// </summary>
public sealed class ProductionSyncTestHost : IAsyncDisposable
{
    private static readonly TimeSpan ShutdownTimeout = TimeSpan.FromSeconds(2);
    private readonly ServiceProvider _services;
    private readonly string _rootDirectory;
    private readonly FakeBackendExecutionProfileProvider _executionProfiles;

    static ProductionSyncTestHost() => Batteries_V2.Init();

    private ProductionSyncTestHost(
        ServiceProvider services,
        string rootDirectory,
        InProcessEnrollmentTransportClientService transport,
        FakeBackendExecutionProfileProvider executionProfiles)
    {
        _services = services;
        _rootDirectory = rootDirectory;
        Transport = transport;
        _executionProfiles = executionProfiles;
    }

    public IServiceProvider Services => _services;
    public InProcessEnrollmentTransportClientService Transport { get; }
    public IEndpoints Endpoints => _services.GetRequiredService<IEndpoints>();
    public IDeviceIdentityService Identity => _services.GetRequiredService<IDeviceIdentityService>();
    public IDeviceSyncTaskService SyncTasks => _services.GetRequiredService<IDeviceSyncTaskService>();
    public SyncPeerProtocolHandler ProtocolHandler => _services.GetRequiredService<SyncPeerProtocolHandler>();
    public IDevicePresenceRegistry Presence => _services.GetRequiredService<IDevicePresenceRegistry>();
    public IDevicePresenceProbeService PresenceProbe => _services.GetRequiredService<IDevicePresenceProbeService>();
    public IDiscoveredDeviceEndpointRegistry EndpointsCache => _services.GetRequiredService<IDiscoveredDeviceEndpointRegistry>();

    public static async Task<ProductionSyncTestHost> CreateAsync()
    {
        var rootDirectory = Path.Combine(
            Path.GetTempPath(),
            "PasswordManagerLocal.SyncEndToEndTests",
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(rootDirectory);

        var executionProfiles = new FakeBackendExecutionProfileProvider();
        executionProfiles.SetProfile(
            new BackendExecutionProfile(
                TimeSpan.FromSeconds(15),
                TimeSpan.FromSeconds(15),
                TimeSpan.FromSeconds(35)),
            isInteractive: true);

        var transport = new InProcessEnrollmentTransportClientService();
        var services = new ServiceCollection();
        services.AddPasswordManagerLocalBackend(
            new BackendStoragePaths(rootDirectory),
            new TestKeyProtector(),
            new FakeLocalDiscoveryNetworkLease(),
            executionProfiles);

        // RelationshipIntegrityMaterializationInterceptor is stateless. A stable shared instance
        // prevents each isolated test host from forcing EF Core to build another equivalent
        // internal service provider.
        services.Replace(ServiceDescriptor.Singleton<RelationshipIntegrityMaterializationInterceptor>(
            ProductionTestServiceInstances.RelationshipIntegrityInterceptor));

        services.Replace(ServiceDescriptor.Singleton<IDeviceIdentityService>(provider =>
            new DeviceIdentityService(
                provider.GetRequiredService<IServiceScopeFactory>(),
                () => DeviceType.WindowsPc)));
        services.Replace(ServiceDescriptor.Singleton<ISyncTransportClientService>(transport));
        services.Replace(ServiceDescriptor.Singleton<ILocalNetworkAddressService>(new FakeLocalNetworkAddressService
        {
            PreferredLocalHosts = ["127.0.0.1"],
            RemoteEndpointPriority = 5000
        }));
        services.Replace(ServiceDescriptor.Singleton<ISyncRuntimeService>(provider =>
            new EnrollmentAwareFakeSyncRuntimeService(
                provider.GetRequiredService<PasswordManagerLocal.Common.Backend.Abstractions.State.IEnrollmentRuntimeState>())));
        services.Replace(ServiceDescriptor.Singleton<ILocalDiscoveryTransport>(new FakeLocalDiscoveryTransport()));

        var provider = services.BuildServiceProvider(new ServiceProviderOptions
        {
            ValidateOnBuild = true,
            ValidateScopes = true
        });
        var host = new ProductionSyncTestHost(provider, rootDirectory, transport, executionProfiles);

        try
        {
            await provider.GetRequiredService<IBackendInitializationService>().InitializeAsync();
            await provider.GetRequiredService<IInteractiveSessionStateService>().ActivateAsync();
            provider.GetRequiredService<DeviceEnrollmentService>().OpenInteractiveAdmission();
            transport.BindLocalBackend(
                provider.GetRequiredService<SyncPeerProtocolHandler>(),
                provider.GetRequiredService<IDeviceIdentityService>(),
                provider.GetRequiredService<IDevicePresenceRegistry>());
            return host;
        }
        catch
        {
            await host.DisposeAsync();
            throw;
        }
    }

    public void ConnectTo(ProductionSyncTestHost remote) =>
        Transport.ConnectTo(remote.ProtocolHandler, remote.Identity);

    public async Task EnterBackgroundOnlyAsync(CancellationToken ct = default)
    {
        var enrollment = _services.GetService<DeviceEnrollmentService>();
        if (enrollment is not null)
            await enrollment.CloseInteractiveAdmissionAsync(ct);

        var interactive = _services.GetRequiredService<IInteractiveSessionStateService>();
        if (interactive.IsActive)
            await interactive.DeactivateAsync(ct);

        _executionProfiles.SetProfile(
            new BackendExecutionProfile(
                TimeSpan.FromSeconds(60),
                TimeSpan.FromSeconds(60),
                TimeSpan.FromSeconds(125)),
            isInteractive: false);
    }

    public async Task<Device> CacheEndpointForAsync(
        ProductionSyncTestHost remote,
        string host = "127.0.0.1",
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(remote);
        using var scope = _services.CreateScope();
        var device = await scope.ServiceProvider
            .GetRequiredService<IDeviceRepository>()
            .GetByIdWithUserDevicesAsync(remote.Identity.LocalDeviceId, ct)
            ?? throw new InvalidOperationException("The remote enrolled device is missing from the local database.");

        EndpointsCache.AddOrUpdate(new DiscoveredDeviceEndpoint
        {
            Host = host,
            Port = 26688,
            TlsCertFingerprint = remote.Identity.FingerprintHex
        });
        _services.GetRequiredService<ISyncDeviceIdentityService>().TryAdd(device);
        return device;
    }

    public Task<DevicePresenceProbeResult> ProbeAsync(Device remoteDevice, bool force = true, CancellationToken ct = default) =>
        PresenceProbe.ProbeAsync(remoteDevice, force, ct);

    public bool IsOnline(ProductionSyncTestHost remote) =>
        _services.GetRequiredService<DeviceOnlineStatusEvaluator>().IsOnline(remote.Identity.FingerprintHex);

    public RegistrationRequest CreateRegistrationRequest(string username) =>
        new()
        {
            Username = username,
            Password = CreatePassword(),
            FirstName = "Regular",
            LastName = "Synchronization",
            Email = $"{username}@example.com",
            RememberMe = false
        };

    public LoginRequest CreateLoginRequest(string username) =>
        new()
        {
            Username = username,
            Password = CreatePassword(),
            RememberMe = false
        };

    public async Task EnableSyncAsync(Guid token, CancellationToken ct = default)
    {
        await Endpoints.SetLocalUserSyncOnAsync(token, true, ct);

        // The test runtime intentionally does not start TCP/UDP hosted services. Set the production
        // device identity state explicitly so outgoing builders and incoming protocol authorization
        // exercise their normal synchronization-enabled checks.
        if (!Identity.IsSyncOn)
            await Identity.SetSyncOnAsync(true, ct);
    }

    public async Task ActivateImportedSynchronizationAsync(CancellationToken ct = default)
    {
        // Enrollment imports the local user-device link as synchronization-enabled. The production
        // runtime refreshes the device identity immediately after import; the test runtime has no OS
        // hosted services, so mirror that refresh without requiring a password login.
        if (!Identity.IsSyncOn)
            await Identity.SetSyncOnAsync(true, ct);
    }

    public async Task<UserCanonicalCheckpoint?> GetOnlyCanonicalCheckpointAsync(CancellationToken ct = default)
    {
        using var scope = _services.CreateScope();
        var users = scope.ServiceProvider.GetRequiredService<IUserRepository>();
        var userIds = await users.ListUserIdsAsync(ct);
        if (userIds.Count != 1)
            throw new InvalidOperationException("The regular-sync test host must contain exactly one user.");
        return await scope.ServiceProvider
            .GetRequiredService<IUserCanonicalCheckpointRepository>()
            .GetAsync(userIds[0], ct);
    }

    public async Task<UserLoginIdentityState> GetOnlyLoginIdentityAsync(CancellationToken ct = default)
    {
        using var scope = _services.CreateScope();
        var identities = await scope.ServiceProvider
            .GetRequiredService<IUserRepository>()
            .ListLoginIdentityStatesAsync(ct);
        if (identities.Count != 1)
            throw new InvalidOperationException("The regular-sync test host must contain exactly one login projection.");
        return identities[0];
    }

    public async Task<bool> StartSyncToAsync(
        ProductionSyncTestHost remote,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(remote);
        ct.ThrowIfCancellationRequested();

        using var scope = _services.CreateScope();
        var device = await scope.ServiceProvider
            .GetRequiredService<IDeviceRepository>()
            .GetByIdWithUserDevicesAsync(remote.Identity.LocalDeviceId, ct)
            ?? throw new InvalidOperationException("The remote enrolled device is missing from the local database.");

        var endpoint = new DiscoveredDeviceEndpoint
        {
            Host = "127.0.0.1",
            Port = 26688,
            TlsCertFingerprint = remote.Identity.FingerprintHex
        };
        var endpointRegistry = _services.GetRequiredService<IDiscoveredDeviceEndpointRegistry>();
        endpointRegistry.AddOrUpdate(endpoint);
        _services.GetRequiredService<ISyncDeviceIdentityService>().TryAdd(device);

        try
        {
            return SyncTasks.TryStart(endpoint, device);
        }
        finally
        {
            // The worker receives its own endpoint copy. Remove the discovery cache entry so a
            // later test mutation cannot auto-start synchronization before the test explicitly
            // kicks the next phase. This keeps data arrangement deterministic while preserving
            // the production queue activation and worker implementations.
            endpointRegistry.TryRemove(endpoint.TlsCertFingerprint);
        }
    }

    public Task WaitForSyncIdleAsync(Guid remoteDeviceId, CancellationToken ct = default) =>
        SyncTasks.WaitForIdleAsync(remoteDeviceId, ct);

    public async Task<bool> HasPendingForAsync(Guid remoteDeviceId, CancellationToken ct = default)
    {
        using var scope = _services.CreateScope();
        return await scope.ServiceProvider
            .GetRequiredService<ISyncQueueRepository>()
            .HasPendingForDeviceAsync(remoteDeviceId, ct);
    }

    public async Task PublishCurrentUserSnapshotAsync(CancellationToken ct = default)
    {
        using var scope = _services.CreateScope();
        var users = scope.ServiceProvider.GetRequiredService<IUserRepository>();
        var userIds = await users.ListUserIdsAsync(ct);
        if (userIds.Count != 1)
            throw new InvalidOperationException("The regular-sync test host must contain exactly one user.");

        var user = await users.GetByIdAsync(userIds[0], ct)
            ?? throw new InvalidOperationException("The regular-sync test user could not be loaded.");
        await scope.ServiceProvider
            .GetRequiredService<IUserSnapshotPublisherService>()
            .GetOrCreateAsync(user, ct);
    }

    public async Task<int> DeletePendingForAsync(Guid remoteDeviceId, CancellationToken ct = default)
    {
        using var scope = _services.CreateScope();
        var queue = scope.ServiceProvider.GetRequiredService<ISyncQueueRepository>();
        var pending = await queue.ListPendingForDeviceWithItemsAsync(remoteDeviceId, ct);
        foreach (var item in pending)
            queue.Delete(item);

        await scope.ServiceProvider.GetRequiredService<IUnitOfWork>().SaveChangesAsync(ct);
        return pending.Count;
    }

    public async ValueTask DisposeAsync()
    {
        Transport.Disconnect();

        var syncTasksStopped = await TryRunCleanupAsync(
            "stop regular synchronization workers",
            ct => SyncTasks.StopAllAsync(ct));

        var enrollmentClosed = await TryRunCleanupAsync(
            "close interactive enrollment admission",
            async ct =>
            {
                var enrollment = _services.GetService<DeviceEnrollmentService>();
                if (enrollment is not null)
                    await enrollment.CloseInteractiveAdmissionAsync(ct);
            });

        var interactiveDeactivated = await TryRunCleanupAsync(
            "deactivate interactive backend session",
            async ct =>
            {
                var interactive = _services.GetService<IInteractiveSessionStateService>();
                if (interactive?.IsActive == true)
                    await interactive.DeactivateAsync(ct);
            });

        var providerDisposed = await TryRunCleanupAsync(
            "dispose backend service provider",
            async _ => await _services.DisposeAsync());

        if (syncTasksStopped && enrollmentClosed && interactiveDeactivated && providerDisposed)
        {
            try
            {
                if (Directory.Exists(_rootDirectory))
                    Directory.Delete(_rootDirectory, recursive: true);
            }
            catch
            {
            }
        }
    }

    private async Task<bool> TryRunCleanupAsync(
        string step,
        Func<CancellationToken, Task> cleanup)
    {
        using var cancellation = new CancellationTokenSource();
        var cleanupTask = Task.Run(() => cleanup(cancellation.Token));

        try
        {
            await cleanupTask.WaitAsync(ShutdownTimeout);
            return true;
        }
        catch (TimeoutException)
        {
            _ = Task.Run(() =>
            {
                try
                {
                    cancellation.Cancel();
                }
                catch
                {
                }
            });
            _ = cleanupTask.ContinueWith(
                static task => _ = task.Exception,
                CancellationToken.None,
                TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously,
                TaskScheduler.Default);
            Console.Error.WriteLine(
                $"Regular-sync test host cleanup step '{step}' timed out. Temporary data was retained at '{_rootDirectory}'.");
            return false;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine(
                $"Regular-sync test host cleanup step '{step}' failed: {ex.Message}. Temporary data was retained at '{_rootDirectory}'.");
            return false;
        }
    }

    private static byte[] CreatePassword() => Encoding.UTF8.GetBytes("P@ssw0rd12345678");
}
