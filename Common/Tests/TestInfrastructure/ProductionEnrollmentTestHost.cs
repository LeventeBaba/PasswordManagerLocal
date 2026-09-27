using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using PasswordManagerLocal.Common.Contracts.Endpoints;
using PasswordManagerLocal.Common.Backend.Abstractions.Services;
using PasswordManagerLocal.Common.Backend.Configuration;
using PasswordManagerLocal.Common.Backend.DependencyInjection;
using PasswordManagerLocal.Common.Backend.Models;
using PasswordManagerLocal.Common.Backend.Persistence;
using PasswordManagerLocal.Common.Contracts.Requests;
using PasswordManagerLocal.Common.Backend.Services;
using PasswordManagerLocal.Common.Backend.Sync.Tcp;
using PasswordManagerLocal.Common.Tests.Fakes;
using SQLitePCL;
using System.Text;

namespace PasswordManagerLocal.Common.Tests.TestInfrastructure;

/// <summary>
/// Creates an isolated backend using production repositories, SQLite, version clock, enrollment
/// services, cryptography, and protocol handlers. Only operating-system networking is replaced.
/// </summary>
public sealed class ProductionEnrollmentTestHost : IAsyncDisposable
{
    private static readonly TimeSpan ShutdownTimeout = TimeSpan.FromSeconds(1);
    private readonly ServiceProvider _services;
    private readonly string _rootDirectory;

    static ProductionEnrollmentTestHost() => Batteries_V2.Init();

    private ProductionEnrollmentTestHost(
        ServiceProvider services,
        string rootDirectory,
        InProcessEnrollmentTransportClientService transport)
    {
        _services = services;
        _rootDirectory = rootDirectory;
        Transport = transport;
    }

    public IServiceProvider Services => _services;
    public InProcessEnrollmentTransportClientService Transport { get; }
    public IEndpoints Endpoints => _services.GetRequiredService<IEndpoints>();
    public IDeviceIdentityService Identity => _services.GetRequiredService<IDeviceIdentityService>();
    public SyncPeerProtocolHandler ProtocolHandler => _services.GetRequiredService<SyncPeerProtocolHandler>();

    public static async Task<ProductionEnrollmentTestHost> CreateAsync()
    {
        var rootDirectory = Path.Combine(
            Path.GetTempPath(),
            "PasswordManagerLocal.EnrollmentTests",
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
        services.Replace(ServiceDescriptor.Singleton<IDeviceSyncTaskService, FakeDeviceSyncTaskService>());
        services.Replace(ServiceDescriptor.Singleton<ILocalDiscoveryTransport>(new FakeLocalDiscoveryTransport()));

        var provider = services.BuildServiceProvider(new ServiceProviderOptions
        {
            ValidateOnBuild = true,
            ValidateScopes = true
        });
        var host = new ProductionEnrollmentTestHost(provider, rootDirectory, transport);

        try
        {
            await provider.GetRequiredService<IBackendInitializationService>().InitializeAsync();
            await provider.GetRequiredService<IInteractiveSessionStateService>().ActivateAsync();
            provider.GetRequiredService<DeviceEnrollmentService>().OpenInteractiveAdmission();
            transport.BindLocalBackend(
                provider.GetRequiredService<SyncPeerProtocolHandler>(),
                provider.GetRequiredService<IDeviceIdentityService>());
            return host;
        }
        catch
        {
            await host.DisposeAsync();
            throw;
        }
    }

    public void ConnectEnrollmentTransportTo(ProductionEnrollmentTestHost remote) =>
        Transport.ConnectTo(remote.ProtocolHandler, remote.Identity);

    public RegistrationRequest CreateRegistrationRequest(string username) =>
        new()
        {
            Username = username,
            Password = CreatePassword(),
            FirstName = "Enrollment",
            LastName = "Integration",
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

    public async ValueTask DisposeAsync()
    {
        // Break cross-host references first. A timed-out enrollment operation must not keep the
        // other backend rooted while this host is being torn down.
        Transport.Disconnect();

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

        // Do not remove the database directory while a timed-out provider disposal may still be
        // using it. Keeping an isolated temporary directory is preferable to racing SQLite cleanup.
        if (enrollmentClosed && interactiveDeactivated && providerDisposed)
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
        var cancellation = new CancellationTokenSource();
        Task cleanupTask;

        try
        {
            // Invoke the cleanup delegate on a worker. Several backend shutdown methods perform
            // synchronous cancellation before their first await; invoking them directly would let
            // those callbacks block the test thread before a timeout can be observed.
            cleanupTask = Task.Run(() => cleanup(cancellation.Token));
        }
        catch (Exception ex)
        {
            cancellation.Dispose();
            Console.Error.WriteLine(
                $"Enrollment test host cleanup step '{step}' could not start: {ex.Message}. " +
                $"Temporary data was retained at '{_rootDirectory}'.");
            return false;
        }

        try
        {
            await cleanupTask.WaitAsync(ShutdownTimeout);
            cancellation.Dispose();
            return true;
        }
        catch (TimeoutException)
        {
            // Do not wait for cancellation callbacks or for the blocked cleanup task. The purpose of
            // this harness cleanup is to keep a failed phase from being replaced by MSTest's outer
            // timeout. The isolated provider/database can finish unwinding in the background.
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
                task =>
                {
                    _ = task.Exception;
                    cancellation.Dispose();
                },
                CancellationToken.None,
                TaskContinuationOptions.ExecuteSynchronously,
                TaskScheduler.Default);

            Console.Error.WriteLine(
                $"Enrollment test host cleanup step '{step}' timed out after " +
                $"{ShutdownTimeout.TotalSeconds:0} second. Temporary data was retained at " +
                $"'{_rootDirectory}'.");
            return false;
        }
        catch (Exception ex)
        {
            cancellation.Dispose();
            Console.Error.WriteLine(
                $"Enrollment test host cleanup step '{step}' failed: {ex.Message}. " +
                $"Temporary data was retained at '{_rootDirectory}'.");
            return false;
        }
    }

    private static byte[] CreatePassword() => Encoding.UTF8.GetBytes("P@ssw0rd12345678");
}
