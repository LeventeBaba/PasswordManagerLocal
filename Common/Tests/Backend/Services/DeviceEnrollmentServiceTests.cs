using Microsoft.Extensions.DependencyInjection;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using PasswordManagerLocal.Common.Backend.Sync;
using PasswordManagerLocal.Common.Backend.Sync.Enrollment;
using PasswordManagerLocal.Common.Backend.Exceptions;
using PasswordManagerLocal.Common.Backend.Abstractions.Services;
using PasswordManagerLocal.Common.Contracts.Responses;
using PasswordManagerLocal.Common.Backend.Services;
using PasswordManagerLocal.Common.Tests.Fakes;

using MSTestAssert = Microsoft.VisualStudio.TestTools.UnitTesting.Assert;
using PasswordManagerLocal.Common.Backend.Sync.Discovery;

using PasswordManagerLocal.Common.Tests.TestInfrastructure.Services.Fixtures;
namespace PasswordManagerLocal.Common.Tests.Backend.Services;

[TestClass]
public sealed class DeviceEnrollmentServiceTests
{
    [TestMethod]
    [TestCategory("Backend")]
    [TestCategory("Unit")]
    public async Task GetEnrollmentStatus_WithoutSession_ReturnsNone()
    {
        using var setup = CreateService();

        var status = await setup.Service.GetEnrollmentStatusAsync();

        MSTestAssert.AreEqual(DeviceEnrollmentState.None, status.State);
        MSTestAssert.AreEqual(0, setup.Runtime.EndEnrollmentOnlyCalls);
    }

    [TestMethod]
    [TestCategory("Backend")]
    [TestCategory("Unit")]
    public async Task CancelEnrollment_EndsTemporaryRuntimeAndLeavesNoSession()
    {
        using var setup = CreateService();

        await setup.Service.CancelEnrollmentAsync();
        var status = await setup.Service.GetEnrollmentStatusAsync();

        MSTestAssert.AreEqual(1, setup.Runtime.EndEnrollmentOnlyCalls);
        MSTestAssert.AreEqual(DeviceEnrollmentState.None, status.State);
    }

    [TestMethod]
    [TestCategory("Backend")]
    [TestCategory("Unit")]
    public async Task StartEnrollment_BackgroundOnly_RejectsBeforeRuntimeActivation()
    {
        using var setup = CreateService(interactive: false);

        var exception = await Assert.ThrowsAsync<DeviceEnrollmentException>(
            async () => await setup.Service.StartEnrollmentAsync());

        MSTestAssert.AreEqual(DeviceEnrollmentErrorCode.InteractiveSessionRequired, exception.ErrorCode);
        MSTestAssert.AreEqual(0, setup.Runtime.BeginEnrollmentOnlyCalls);
    }

    [TestMethod]
    [TestCategory("Backend")]
    [TestCategory("Unit")]
    public async Task AddDeviceByCode_BackgroundOnly_RejectsBeforeAuthorizationOrMutation()
    {
        using var setup = CreateService(interactive: false);

        var exception = await Assert.ThrowsAsync<DeviceEnrollmentException>(
            async () => await setup.Service.AddDeviceByCodeAsync(Guid.NewGuid(), "unused"));

        MSTestAssert.AreEqual(DeviceEnrollmentErrorCode.InteractiveSessionRequired, exception.ErrorCode);
        MSTestAssert.AreEqual(0, setup.Runtime.BeginEnrollmentOnlyCalls);
    }

    [TestMethod]
    [TestCategory("Backend")]
    [TestCategory("Unit")]
    public async Task CloseInteractiveAdmission_CancelsTransientEnrollmentAndRejectsNewActions()
    {
        using var setup = CreateService();
        await setup.Service.StartEnrollmentAsync();
        MSTestAssert.IsNotNull(setup.LocalDiscovery.ActiveSessionId);

        await setup.Service.CloseInteractiveAdmissionAsync();

        MSTestAssert.IsNull(setup.LocalDiscovery.ActiveSessionId);
        MSTestAssert.AreEqual(1, setup.Runtime.EndEnrollmentOnlyCalls);
        var exception = await Assert.ThrowsAsync<DeviceEnrollmentException>(
            async () => await setup.Service.StartEnrollmentAsync());
        MSTestAssert.AreEqual(DeviceEnrollmentErrorCode.InteractiveSessionRequired, exception.ErrorCode);
    }

    [TestMethod]
    [TestCategory("Backend")]
    [TestCategory("Unit")]
    public async Task CloseInteractiveAdmission_CancelsOperationBeforeCriticalBoundary()
    {
        using var setup = CreateService();
        setup.Runtime.BeginEnrollmentOnlyGate = new TaskCompletionSource<bool>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var start = setup.Service.StartEnrollmentAsync();
        await WaitForAsync(() => setup.Runtime.BeginEnrollmentOnlyCalls == 1);

        var close = setup.Service.CloseInteractiveAdmissionAsync();

        await Assert.ThrowsAsync<OperationCanceledException>(async () => await start);
        await close;

        MSTestAssert.IsNull(setup.LocalDiscovery.ActiveSessionId);
        MSTestAssert.IsTrue(setup.Runtime.EndEnrollmentOnlyCalls >= 1);
    }

    [TestMethod]
    [TestCategory("Backend")]
    [TestCategory("Unit")]
    public async Task CloseInteractiveAdmission_CancelsPreCriticalAdmissionTokenBeforeDrain()
    {
        using var setup = CreateService();
        using var operation = setup.Service.EnterEnrollmentOperation();

        var close = setup.Service.CloseInteractiveAdmissionAsync();

        MSTestAssert.IsTrue(operation.AdmissionCancellationToken.IsCancellationRequested);
        MSTestAssert.IsFalse(close.IsCompleted);
        operation.Dispose();
        await close;
    }

    [TestMethod]
    [TestCategory("Backend")]
    [TestCategory("Unit")]
    public async Task CloseInteractiveAdmission_DrainsOperationPastCriticalBoundary()
    {
        using var setup = CreateService();
        var operation = setup.Service.EnterEnrollmentOperation();
        operation.EnterCriticalSection();

        var close = setup.Service.CloseInteractiveAdmissionAsync();
        MSTestAssert.IsFalse(close.IsCompleted);

        operation.Dispose();
        await close;

        MSTestAssert.AreEqual(1, setup.Runtime.EndEnrollmentOnlyCalls);
    }

    private static EnrollmentServiceSetup CreateService(bool interactive = true)
    {
        var provider = new ServiceCollection().BuildServiceProvider();
        var runtime = new FakeSyncRuntimeService();
        var identity = new FakeDeviceIdentityService
        {
            FingerprintHex = new string('A', 64),
            SignPublicKey = Enumerable.Repeat((byte)0x31, 32).ToArray(),
            AgreementPublicKey = Enumerable.Repeat((byte)0x32, 32).ToArray()
        };
        var endpointRegistry = new DiscoveredDeviceEndpointRegistry();
        var transport = new FakeSyncTransportClientService
        {
            EnrollmentInfoReply = new GetDeviceEnrollmentInfoReply
            {
                Ok = true,
                DeviceId = identity.LocalDeviceId.ToString("N"),
                OriginInstanceId = identity.OriginInstanceId.ToString("N"),
                TlsCertFingerprint = identity.FingerprintHex
            }
        };
        var localDiscovery = new FakeLocalDiscoveryService();
        var networkAddresses = new FakeLocalNetworkAddressService();
        var snapshotService = new DeviceEnrollmentSnapshotService(identity);
        var localLinks = new DeviceEnrollmentLocalLinkService(identity);
        var executionProfileProvider = new FakeBackendExecutionProfileProvider();
        executionProfileProvider.SetProfile(
            interactive
                ? new BackendExecutionProfile(
                    TimeSpan.FromSeconds(15),
                    TimeSpan.FromSeconds(15),
                    TimeSpan.FromSeconds(35))
                : new BackendExecutionProfile(
                    TimeSpan.FromSeconds(60),
                    TimeSpan.FromSeconds(60),
                    TimeSpan.FromSeconds(125)),
            interactive);
        var service = new DeviceEnrollmentService(
            provider.GetRequiredService<IServiceScopeFactory>(),
            identity,
            endpointRegistry,
            runtime,
            localDiscovery,
            new DeviceEnrollmentEndpointService(identity, transport, networkAddresses),
            new DeviceEnrollmentRegistrationService(identity, endpointRegistry, networkAddresses, localLinks),
            snapshotService,
            new DeviceEnrollmentSnapshotTransferService(identity, transport, snapshotService),
            new DeviceEnrollmentSnapshotImporterService(identity, localLinks),
            new FakeInteractiveUserDataStateAccessor(),
            executionProfileProvider);
        if (interactive)
            service.OpenInteractiveAdmission();
        return new EnrollmentServiceSetup(
            service,
            provider,
            runtime,
            executionProfileProvider,
            localDiscovery,
            transport);
    }

    private static async Task WaitForAsync(Func<bool> condition)
    {
        var deadline = DateTimeOffset.UtcNow.AddSeconds(2);
        while (!condition())
        {
            if (DateTimeOffset.UtcNow >= deadline)
                MSTestAssert.Fail("The enrollment operation did not reach the expected state.");

            await Task.Delay(10);
        }
    }

}
