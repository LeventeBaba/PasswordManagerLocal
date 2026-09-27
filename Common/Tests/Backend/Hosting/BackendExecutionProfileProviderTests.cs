using Microsoft.Extensions.DependencyInjection;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using PasswordManagerLocal.Common.Contracts.Endpoints;
using PasswordManagerLocal.Common.Backend.Hosting;
using PasswordManagerLocal.Common.Contracts.Runtime;
using PasswordManagerLocal.Common.Contracts.BackgroundSync;
using PasswordManagerLocal.Common.Tests.Fakes;
using PasswordManagerLocal.Common.Tests.TestInfrastructure;

namespace PasswordManagerLocal.Common.Tests.Backend.Hosting;

[TestClass]
public sealed class BackendExecutionProfileProviderTests
{
    [TestMethod]
    public async Task InteractiveLease_SelectsInteractiveProfile()
    {
        using var host = new BackendTestHost();
        var runtime = new FakeBackendRuntime(host.Services.GetRequiredService<IEndpoints>());
        var coordinator = new BackendRuntimeLifetimeCoordinator(runtime);

        await using var lease = await coordinator.AcquireAsync(BackendLifetimeReason.InteractiveUi);

        AssertInteractive(coordinator.ExecutionProfileProvider);
    }

    [TestMethod]
    public async Task BackgroundLease_SelectsBackgroundProfile()
    {
        using var host = new BackendTestHost();
        var runtime = new FakeBackendRuntime(host.Services.GetRequiredService<IEndpoints>());
        var coordinator = new BackendRuntimeLifetimeCoordinator(runtime);

        await using var lease = await coordinator.AcquireAsync(BackendLifetimeReason.BackgroundSync);

        AssertBackground(coordinator.ExecutionProfileProvider);
    }

    [TestMethod]
    public async Task InteractiveAndBackground_SelectInteractiveProfile()
    {
        using var host = new BackendTestHost();
        var runtime = new FakeBackendRuntime(host.Services.GetRequiredService<IEndpoints>());
        var coordinator = new BackendRuntimeLifetimeCoordinator(runtime);

        await using var background = await coordinator.AcquireAsync(BackendLifetimeReason.BackgroundSync);
        await using var interactive = await coordinator.AcquireAsync(BackendLifetimeReason.InteractiveUi);

        AssertInteractive(coordinator.ExecutionProfileProvider);
    }

    [TestMethod]
    public async Task FinalLeaseRelease_ClearsProfileAndStopsRuntime()
    {
        using var host = new BackendTestHost();
        var runtime = new FakeBackendRuntime(host.Services.GetRequiredService<IEndpoints>());
        var coordinator = new BackendRuntimeLifetimeCoordinator(runtime);
        var lease = await coordinator.AcquireAsync(BackendLifetimeReason.BackgroundSync);

        await lease.DisposeAsync();

        Assert.IsNull(coordinator.ExecutionProfileProvider.Current);
        Assert.IsFalse(coordinator.ExecutionProfileProvider.IsInteractive);
        Assert.IsFalse(coordinator.ExecutionProfileProvider.IsEnrollmentAllowed);
        Assert.AreEqual(1, runtime.StopCalls);
    }

    [TestMethod]
    public async Task DuplicateReasonLease_DoesNotPublishDuplicateProfileChange()
    {
        using var host = new BackendTestHost();
        var runtime = new FakeBackendRuntime(host.Services.GetRequiredService<IEndpoints>());
        var coordinator = new BackendRuntimeLifetimeCoordinator(runtime);
        var notifications = 0;
        coordinator.ExecutionProfileProvider.ProfileChanged += (_, _) => notifications++;

        await using var first = await coordinator.AcquireAsync(BackendLifetimeReason.InteractiveUi);
        await using var second = await coordinator.AcquireAsync(BackendLifetimeReason.InteractiveUi);

        Assert.AreEqual(1, notifications);
    }

    [TestMethod]
    public async Task BackgroundLeaseReleasedWhileInteractiveRemains_KeepsInteractiveProfileWithoutNotification()
    {
        using var host = new BackendTestHost();
        var runtime = new FakeBackendRuntime(host.Services.GetRequiredService<IEndpoints>());
        var coordinator = new BackendRuntimeLifetimeCoordinator(runtime);
        await using var interactive = await coordinator.AcquireAsync(BackendLifetimeReason.InteractiveUi);
        var background = await coordinator.AcquireAsync(BackendLifetimeReason.BackgroundSync);
        var notifications = 0;
        coordinator.ExecutionProfileProvider.ProfileChanged += (_, _) => notifications++;

        await background.DisposeAsync();

        AssertInteractive(coordinator.ExecutionProfileProvider);
        Assert.AreEqual(0, notifications);
    }

    [TestMethod]
    public async Task InteractiveLeaseReleasedWhileBackgroundRemains_SelectsBackgroundAndDisablesEnrollment()
    {
        using var host = new BackendTestHost();
        var runtime = new FakeBackendRuntime(host.Services.GetRequiredService<IEndpoints>());
        var coordinator = new BackendRuntimeLifetimeCoordinator(runtime);
        await using var background = await coordinator.AcquireAsync(BackendLifetimeReason.BackgroundSync);
        var interactive = await coordinator.AcquireAsync(BackendLifetimeReason.InteractiveUi);
        var lifecycle = (IBackendExecutionProfileProviderLifecycle)coordinator.ExecutionProfileProvider;
        lifecycle.OpenEnrollmentAdmission();

        await interactive.DisposeAsync();

        AssertBackground(coordinator.ExecutionProfileProvider);
        Assert.IsFalse(coordinator.ExecutionProfileProvider.IsEnrollmentAllowed);
    }

    [TestMethod]
    public async Task DatabaseResetInitializesInteractiveProfileBeforeRuntimeRestart()
    {
        using var host = new BackendTestHost();
        BackendRuntimeLifetimeCoordinator? coordinator = null;
        var runtime = new FakeBackendRuntime(host.Services.GetRequiredService<IEndpoints>())
        {
            BeforeReset = () => AssertInteractive(coordinator!.ExecutionProfileProvider)
        };
        coordinator = new BackendRuntimeLifetimeCoordinator(runtime);

        await using var lease = await coordinator.ResetDatabaseAndAcquireAsync(
            BackendLifetimeReason.InteractiveUi);

        Assert.AreEqual(1, runtime.ResetCalls);
        AssertInteractive(coordinator.ExecutionProfileProvider);
    }

    [TestMethod]
    public async Task InteractiveSessionAdmission_ControlsEnrollmentAvailability()
    {
        using var host = new BackendTestHost();
        var runtime = new FakeBackendRuntime(host.Services.GetRequiredService<IEndpoints>());
        var coordinator = new BackendRuntimeLifetimeCoordinator(runtime);
        await using var lease = await coordinator.AcquireAsync(BackendLifetimeReason.InteractiveUi);
        var lifecycle = (IBackendExecutionProfileProviderLifecycle)coordinator.ExecutionProfileProvider;

        Assert.IsFalse(coordinator.ExecutionProfileProvider.IsEnrollmentAllowed);
        lifecycle.OpenEnrollmentAdmission();
        Assert.IsTrue(coordinator.ExecutionProfileProvider.IsEnrollmentAllowed);
        lifecycle.CloseEnrollmentAdmission();
        Assert.IsFalse(coordinator.ExecutionProfileProvider.IsEnrollmentAllowed);
    }

    private static void AssertInteractive(PasswordManagerLocal.Common.Backend.Abstractions.Services.IBackendExecutionProfileProvider provider)
    {
        Assert.IsNotNull(provider.Current);
        Assert.AreEqual(TimeSpan.FromSeconds(15), provider.Current.LocalDiscoveryInterval);
        Assert.AreEqual(TimeSpan.FromSeconds(15), provider.Current.NetworkConfigurationPollInterval);
        Assert.AreEqual(TimeSpan.FromSeconds(35), provider.Current.DeviceOnlineTimeout);
        Assert.IsTrue(provider.IsInteractive);
    }

    private static void AssertBackground(PasswordManagerLocal.Common.Backend.Abstractions.Services.IBackendExecutionProfileProvider provider)
    {
        Assert.IsNotNull(provider.Current);
        Assert.AreEqual(TimeSpan.FromSeconds(60), provider.Current.LocalDiscoveryInterval);
        Assert.AreEqual(TimeSpan.FromSeconds(60), provider.Current.NetworkConfigurationPollInterval);
        Assert.AreEqual(TimeSpan.FromSeconds(125), provider.Current.DeviceOnlineTimeout);
        Assert.IsFalse(provider.IsInteractive);
        Assert.IsFalse(provider.IsEnrollmentAllowed);
    }
}
