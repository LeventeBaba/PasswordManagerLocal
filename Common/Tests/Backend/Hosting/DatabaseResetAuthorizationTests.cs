using System.Text;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using PasswordManagerLocal.Common.Backend.Abstractions.Repositories;
using PasswordManagerLocal.Common.Backend.Abstractions.Services;
using PasswordManagerLocal.Common.Backend.Configuration;
using PasswordManagerLocal.Common.Backend.DependencyInjection;
using PasswordManagerLocal.Common.Backend.Hosting;
using PasswordManagerLocal.Common.Backend.Services;
using PasswordManagerLocal.Common.Contracts.Enrollment;
using PasswordManagerLocal.Common.Contracts.Requests;
using PasswordManagerLocal.Common.Contracts.Runtime;
using PasswordManagerLocal.Common.Tests.Fakes;
using PasswordManagerLocal.Common.Tests.TestInfrastructure;

namespace PasswordManagerLocal.Common.Tests.Backend.Hosting;

[TestClass]
[DoNotParallelize]
public sealed class DatabaseResetAuthorizationTests
{
    [TestMethod]
    [Timeout(120_000)]
    [TestCategory("Recovery")]
    [TestCategory("EnrollmentSecurity")]
    public async Task ConfirmedResetRemovesAllLocalProfilesAndCreatesIdentityWithoutMembership()
    {
        using var directory = new TemporaryDirectory();
        var paths = new BackendStoragePaths(directory.Path);
        ServiceProvider? activeServices = null;
        BackendRuntimeLifetimeCoordinator? coordinator = null;
        var options = new BackendRuntimeOptions(paths, static () => new TestKeyProtector(),
            static () => new FakeLocalDiscoveryNetworkLease(), (protector, discovery) =>
            {
                var services = new ServiceCollection();
                services.AddPasswordManagerLocalBackend(paths, protector, discovery, coordinator!.ExecutionProfileProvider);
                services.Replace(ServiceDescriptor.Singleton<IDeviceIdentityService>(provider =>
                    new DeviceIdentityService(provider.GetRequiredService<IServiceScopeFactory>(), () => DeviceType.WindowsPc)));
                services.Replace(ServiceDescriptor.Singleton<ISyncRuntimeService>(provider =>
                    new EnrollmentAwareFakeSyncRuntimeService(provider.GetRequiredService<
                        PasswordManagerLocal.Common.Backend.Abstractions.State.IEnrollmentRuntimeState>())));
                activeServices = services.BuildServiceProvider(new ServiceProviderOptions { ValidateScopes = true });
                return new BackendServiceHost(activeServices);
            });
        await using var runtime = new BackendRuntime(options);
        coordinator = new BackendRuntimeLifetimeCoordinator(runtime);
        Guid firstUser;
        Guid secondUser;
        Guid oldDevice;
        Guid oldOrigin;
        await using (var lease = await coordinator.AcquireAsync(BackendLifetimeReason.InteractiveUi))
        await using (var session = await runtime.OpenInteractiveSessionAsync())
        {
            var token = await session.Endpoints.RegisterAsync(Registration("reset_first"));
            firstUser = (await session.Endpoints.GetUserProfileInfoAsync(token)).UId;
            var other = await session.Endpoints.RegisterAsync(Registration("reset_second"));
            secondUser = (await session.Endpoints.GetUserProfileInfoAsync(other)).UId;
            var identity = activeServices!.GetRequiredService<IDeviceIdentityService>();
            oldDevice = identity.LocalDeviceId;
            oldOrigin = identity.OriginInstanceId;
        }
        // The reset endpoint remains unavailable on a healthy installation. Simulate
        // an actual subsequent unreadable-configuration startup, with its data intact.
        var configuration = await File.ReadAllBytesAsync(paths.DatabaseConfigPath);
        configuration[0] ^= 0xff;
        await File.WriteAllBytesAsync(paths.DatabaseConfigPath, configuration);
        await Assert.ThrowsAsync<DatabaseConfigurationInvalidException>(async () =>
            await coordinator.AcquireAsync(BackendLifetimeReason.InteractiveUi));
        Assert.AreEqual(BackendLifetimeReason.None, coordinator.ActiveReasons);
        await using var resetLease = await coordinator.ResetDatabaseAndAcquireAsync(BackendLifetimeReason.InteractiveUi);
        await using var freshSession = await runtime.OpenInteractiveSessionAsync();
        var freshServices = activeServices
            ?? throw new AssertFailedException("Reset did not create a replacement service host.");
        var freshIdentity = freshServices.GetRequiredService<IDeviceIdentityService>();
        Assert.AreNotEqual(oldDevice, freshIdentity.LocalDeviceId);
        Assert.AreNotEqual(oldOrigin, freshIdentity.OriginInstanceId);
        using var scope = freshServices.CreateScope();
        var users = scope.ServiceProvider.GetRequiredService<IUserRepository>();
        Assert.AreEqual(0, (await users.ListUserIdsAsync()).Count);
        var authorizations = scope.ServiceProvider.GetRequiredService<IUserMembershipAuthorizationRepository>();
        Assert.AreEqual(0, (await authorizations.ListForUserAsync(firstUser)).Count);
        Assert.AreEqual(0, (await authorizations.ListForUserAsync(secondUser)).Count);
        Assert.AreEqual(0, (await freshSession.Endpoints.RestoreRememberedSessionsAsync()).Count);
        Assert.AreEqual(DeviceEnrollmentState.None,
            (await freshSession.Endpoints.GetDeviceEnrollmentStatusAsync()).State);
        Assert.IsFalse(File.Exists(paths.DatabaseResetPendingPath));
    }

    private static RegistrationRequest Registration(string username) => new()
    {
        Username = username, Password = Encoding.UTF8.GetBytes("P@ssw0rd12345678"),
        FirstName = "Recovery", LastName = "Test", Email = $"{username}@example.com", RememberMe = true
    };

    private sealed class TemporaryDirectory : IDisposable
    {
        public string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(),
            $"PasswordManagerLocal.ResetAuthorization.{Guid.NewGuid():N}");
        public TemporaryDirectory() => Directory.CreateDirectory(Path);
        public void Dispose()
        {
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            if (Directory.Exists(Path)) Directory.Delete(Path, recursive: true);
        }
    }

}
