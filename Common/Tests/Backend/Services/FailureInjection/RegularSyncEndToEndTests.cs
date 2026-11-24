using Microsoft.Extensions.DependencyInjection;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using PasswordManagerLocal.Common.Backend.Abstractions.Repositories;
using PasswordManagerLocal.Common.Backend.Abstractions.Services;
using PasswordManagerLocal.Common.Backend.Abstractions.State;
using PasswordManagerLocal.Common.Backend.Abstractions.Sync.Discovery;
using PasswordManagerLocal.Common.Backend.Models;
using PasswordManagerLocal.Common.Backend.Services;
using PasswordManagerLocal.Common.Backend.Sync;
using PasswordManagerLocal.Common.Tests.TestInfrastructure;
using PasswordManagerLocal.Common.Tests.TestInfrastructure.Storage;
using System.Text;

namespace PasswordManagerLocal.Common.Tests.Backend.Services;

public sealed partial class RegularSyncEndToEndTests
{
    [TestMethod]
    [TestCategory("Backend")]
    [TestCategory("Integration")]
    [Timeout(ThreeDeviceTestTimeoutMilliseconds)]
    [TestCategory("Security")]
    [TestCategory("Revocation")]
    [TestCategory("DatabaseGrowthBaseline")]
    public async Task FinalDeletionReceipt_RestartBeforeExport_RetainsDebtAndConvergesThroughRelay()
    {
        await using var setup = await CreateThreeDeviceSetupAsync();
        await SynchronizeThirdMembershipAsync(setup);
        var userId = GetLifecycleUserId(setup);
        foreach (var host in setup.Hosts) host.Transport.Disconnect();
        await setup.Original.Endpoints.DeleteUserAccountAsync(setup.OriginalToken, Encoding.UTF8.GetBytes("P@ssw0rd12345678"));
        UserControlOperationEnvelope operation;
        using (var scope = setup.Original.Services.CreateScope())
        {
            var row = (await scope.ServiceProvider.GetRequiredService<IUserControlOperationRepository>().ListForUserAsync(userId))
                .Single(row => row.OperationType == UserControlOperationType.AccountDeletion);
            operation = UserControlOperationEnvelopeUtil.Deserialize(row.EnvelopePayload);
        }
        // Persist the incoming authenticated operation but interrupt the connection before any
        // application receipt/custody response can reach the sender. No transport is connected.
        foreach (var recipient in new[] { setup.Middle, setup.NewDevice })
        {
            using var scope = recipient.Services.CreateScope();
            var receipt = await scope.ServiceProvider.GetRequiredService<IUserControlOperationInboxService>()
                .StoreAndApplyAsync(operation, setup.Original.Identity.LocalDeviceId);
            Assert.AreEqual(UserControlOperationReceiptState.Applied, receipt.State);
        }
        Assert.IsFalse(await HasRevocationReceiptFromAsync(setup.Original, operation.OperationId, setup.NewDevice));
        await using var restarted = await setup.NewDevice.RestartAsync();
        Assert.IsTrue(await HasRevocationReceiptFromAsync(restarted, operation.OperationId, restarted));
        using (var scope = restarted.Services.CreateScope())
        {
            var progress = (await scope.ServiceProvider.GetRequiredService<IRevocationDeliveryService>().GetSnapshotsAsync())
                .Single(row => row.OperationId == operation.OperationId);
            Assert.IsTrue(progress.HasUnexportedEvidence);
            Assert.AreNotEqual(RevocationDeliveryState.Completed, progress.State);
            Assert.IsNotNull(await scope.ServiceProvider.GetRequiredService<IDeletedUserBarrierRepository>().GetAsync(userId));
        }
        var runtime = CreateControlValidationRuntime(restarted);
        try
        {
            await runtime.RefreshSyncEnabledAsync();
            Assert.AreEqual(SyncRuntimeState.Running, runtime.Snapshot.State);
            Assert.IsTrue(runtime.Snapshot.Reasons.HasFlag(SyncRuntimeReason.PendingRevocationDelivery));
            Assert.IsFalse(runtime.Snapshot.Reasons.HasFlag(SyncRuntimeReason.NormalProfileSynchronization));
            setup.Original.ConnectTo(setup.Middle); setup.Middle.ConnectTo(setup.Original);
            setup.Middle.ConnectTo(restarted); restarted.ConnectTo(setup.Middle);
            using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(60));
            for (var pass = 0; pass < 6; pass++)
            {
                await StartSyncAsync(restarted, setup.Middle, "resume interrupted receipt export", cancellation.Token);
                await StartSyncAsync(setup.Middle, setup.Original, "relay receipt to author", cancellation.Token);
                await StartSyncAsync(setup.Original, setup.Middle, "return author custody", cancellation.Token);
                await StartSyncAsync(setup.Middle, restarted, "return relay custody", cancellation.Token);
            }
            foreach (var host in new[] { setup.Original, setup.Middle, restarted })
            {
                using var scope = host.Services.CreateScope();
                var progress = (await scope.ServiceProvider.GetRequiredService<IRevocationDeliveryService>().GetSnapshotsAsync())
                    .Single(row => row.OperationId == operation.OperationId);
                Assert.AreEqual(RevocationDeliveryState.Completed, progress.State);
                Assert.IsNull(await scope.ServiceProvider.GetRequiredService<IUserRepository>().GetByIdAsync(userId));
                Assert.IsNotNull(await scope.ServiceProvider.GetRequiredService<IDeletedUserBarrierRepository>().GetAsync(userId));
            }
            var before = await SqliteStorageMeasurement.CaptureAsync(restarted);
            before.WriteTo(TestContext, "completed-receipt-exchange-before");
            for (var pass = 0; pass < 8; pass++)
            {
                await StartSyncAsync(restarted, setup.Middle, "repeat completed receipt inventory", cancellation.Token);
                await StartSyncAsync(setup.Middle, restarted, "repeat completed custody inventory", cancellation.Token);
            }
            var after = await SqliteStorageMeasurement.CaptureAsync(restarted);
            after.WriteTo(TestContext, "completed-receipt-exchange-after-8");
            before.AssertSameRows(after, "RevocationReceiptEvidence", "RevocationDeliveryReceipts", "RevocationEvidenceDebts",
                "UserControlOperations", "DeletedUserBarriers");
            await runtime.RefreshSyncEnabledAsync();
            Assert.AreEqual(SyncRuntimeState.Disabled, runtime.Snapshot.State);
            Assert.IsFalse(runtime.Snapshot.Reasons.HasFlag(SyncRuntimeReason.NormalProfileSynchronization));
        }
        finally { await runtime.StopAsync(); }
    }

    private static SyncRuntimeService CreateControlValidationRuntime(ProductionSyncTestHost host)
    {
        var services = host.Services;
        return new(services.GetRequiredService<IServiceScopeFactory>(), host.Identity,
            services.GetRequiredService<IEnrollmentRuntimeState>(), services.GetRequiredService<ISyncDeviceIdentityService>(),
            services.GetRequiredService<IDiscoveredDeviceEndpointRegistry>(), host.SyncTasks, []);
    }
}
