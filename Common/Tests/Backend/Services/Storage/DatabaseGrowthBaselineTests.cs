using Microsoft.Extensions.DependencyInjection;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using PasswordManagerLocal.Common.Backend.Abstractions.Repositories;
using PasswordManagerLocal.Common.Backend.Abstractions.Services;
using PasswordManagerLocal.Common.Backend.Models;
using PasswordManagerLocal.Common.Contracts.Requests;
using PasswordManagerLocal.Common.Tests.TestInfrastructure;
using PasswordManagerLocal.Common.Tests.TestInfrastructure.Storage;
using System.Text;

namespace PasswordManagerLocal.Common.Tests.Backend.Services;

[TestClass]
[DoNotParallelize]
[TestCategory("Backend")]
[TestCategory("Integration")]
[TestCategory("DatabaseGrowthBaseline")]
public sealed class DatabaseGrowthBaselineTests
{
    public TestContext TestContext { get; set; } = null!;

    [TestMethod]
    [TestCategory("SyncStressExtended")]
    [Timeout(180_000)]
    public async Task EnrollmentRemovalCycles_WithBoundedLiveMembership_RecordRetainedAuthorityGrowth()
    {
        if (Environment.GetEnvironmentVariable("PASSWORDMANAGERLOCAL_SYNC_STRESS") != "1")
            Assert.Inconclusive("Set PASSWORDMANAGERLOCAL_SYNC_STRESS=1 to measure enrollment/removal churn.");
        await using var source = await ProductionSyncTestHost.CreateAsync();
        var token = await source.Endpoints.RegisterAsync(source.CreateRegistrationRequest("growth-membership"));
        await source.EnableSyncAsync(token);
        await source.Endpoints.GetSavedPasswordsAsync(token);
        (await SqliteStorageMeasurement.CaptureAsync(source)).WriteTo(TestContext, "membership-before");
        for (var round = 0; round < 4; round++)
        {
            await using var target = await ProductionSyncTestHost.CreateAsync();
            source.ConnectTo(target); target.ConnectTo(source);
            try
            {
                var code = await target.Endpoints.StartDeviceEnrollmentAsync();
                await source.Endpoints.AddDeviceByCodeAsync(token, code.Code);
                Assert.IsTrue((await source.Endpoints.GetUserDevicesAsync(token))
                    .Any(device => device.DeviceId == target.Identity.LocalDeviceId));
                var removal = await source.Endpoints.DisconnectUserDeviceAsync(token, target.Identity.LocalDeviceId,
                    source.CreateLoginRequest("growth-membership").Password);
                Assert.IsTrue(removal.Removed);
                Assert.IsTrue(removal.OperationId.HasValue);
                source.EndpointsCache.Clear();
                await source.WaitForSyncIdleAsync(target.Identity.LocalDeviceId).WaitAsync(TimeSpan.FromSeconds(10));
                Assert.AreEqual(1, (await source.Endpoints.GetUserDevicesAsync(token)).Count);
                var measurement = await SqliteStorageMeasurement.CaptureAsync(source);
                measurement.WriteTo(TestContext, $"membership-cycle-{round + 1}");
                // Retained signatures/cutoffs are intentionally measured, never erased to pass a size assertion.
                Assert.IsTrue(measurement.Rows["UserOriginRemovalCutoffs"] >= round + 1);
            }
            finally { source.Transport.Disconnect(); target.Transport.Disconnect(); source.EndpointsCache.Clear(); }
        }
    }

    [TestMethod]
    public async Task RejectedLogin_DoesNotAllocatePersistentSecurityEvidence()
    {
        await using var host = await ProductionSyncTestHost.CreateAsync();
        var token = await host.Endpoints.RegisterAsync(host.CreateRegistrationRequest("growth-rejected-login"));
        await host.Endpoints.GetSavedPasswordsAsync(token);
        await host.Endpoints.LogoutAsync(token);
        var before = await SqliteStorageMeasurement.CaptureAsync(host);
        before.WriteTo(TestContext, "failed-auth-before");
        for (var index = 0; index < 6; index++)
        {
            var request = host.CreateLoginRequest("growth-rejected-login");
            request.Password[0] ^= 1;
            await Assert.ThrowsExactlyAsync<UnauthorizedAccessException>(() => host.Endpoints.LoginAsync(request));
        }
        var after = await SqliteStorageMeasurement.CaptureAsync(host);
        after.WriteTo(TestContext, "failed-auth-after-6");
        before.AssertSameRows(after);
    }

    [TestMethod]
    public async Task RecoveryWithoutEvidence_ReusesFaultIdentity_AndNeverPublishesDamagedCanonicalData()
    {
        await using var host = await ProductionSyncTestHost.CreateAsync();
        var token = await host.Endpoints.RegisterAsync(host.CreateRegistrationRequest("growth-recovery"));
        await host.Endpoints.GetSavedPasswordsAsync(token);
        using var scope = host.Services.CreateScope();
        var services = scope.ServiceProvider;
        var userId = services.GetRequiredService<IUserSessionService>().GetUidFromToken(token);
        using var key = services.GetRequiredService<IUserSessionService>().GetEncryptionKeyFromToken(token);
        var snapshots = services.GetRequiredService<IUserSyncSnapshotRepository>();
        snapshots.DeleteRange(await snapshots.ListForUserAsync(userId));
        await services.GetRequiredService<PasswordManagerLocal.Common.Backend.Abstractions.Persistence.IUnitOfWork>().SaveChangesAsync();
        var user = (await services.GetRequiredService<IUserRepository>().GetByIdAsync(userId))!;
        var damaged = user.EncryptedPayload.ToArray();
        damaged[0] ^= 0x5A;
        await services.ExecuteFixtureSqlAsync("""UPDATE "Users" SET "EncryptedPayload" = $payload WHERE "UId" = $user;""",
            ("$payload", damaged), ("$user", userId));
        var recovery = services.GetRequiredService<IUserDataRecoveryCoordinator>();
        Assert.AreEqual(UserDataRecoveryState.AwaitingPeerEvidence, (await recovery.TryRecoverAsync(userId, key,
            UserSyncKeyConfidence.AuthenticatedSession, UserDataRecoveryTrigger.ManualRetry)).State);
        var before = await SqliteStorageMeasurement.CaptureAsync(host);
        before.WriteTo(TestContext, "no-evidence-recovery-before");
        for (var index = 0; index < 12; index++)
        {
            Assert.AreEqual(UserDataRecoveryState.AwaitingPeerEvidence, (await recovery.TryRecoverAsync(userId, key,
                UserSyncKeyConfidence.AuthenticatedSession, UserDataRecoveryTrigger.ManualRetry)).State);
        }
        var after = await SqliteStorageMeasurement.CaptureAsync(host);
        after.WriteTo(TestContext, "no-evidence-recovery-after-12");
        before.AssertSameRows(after);
        Assert.AreEqual(0L, after.Rows["UserSyncSnapshots"]);
        using var readback = host.Services.CreateScope();
        var persisted = (await readback.ServiceProvider.GetRequiredService<IUserRepository>().GetByIdAsNoTrackingAsync(userId))!;
        CollectionAssert.AreEqual(damaged, persisted.EncryptedPayload);
        var faults = await readback.ServiceProvider.GetRequiredService<IUserSyncFaultRepository>().ListActiveForUserAsync(userId);
        Assert.IsTrue(faults.Any(fault => fault.BlocksPublishing && fault.BlocksLogin));
    }

    [TestMethod]
    public async Task UnchangedPublicationEligibilityAndHealthyRecovery_DoNotAllocatePersistentRows()
    {
        await using var host = await ProductionSyncTestHost.CreateAsync();
        var token = await host.Endpoints.RegisterAsync(host.CreateRegistrationRequest("growth-noop"));
        await host.Endpoints.GetSavedPasswordsAsync(token);
        using var scope = host.Services.CreateScope();
        var services = scope.ServiceProvider;
        var sessions = services.GetRequiredService<IUserSessionService>();
        var userId = sessions.GetUidFromToken(token);
        using var key = sessions.GetEncryptionKeyFromToken(token);
        var users = services.GetRequiredService<IUserRepository>();
        var publisher = services.GetRequiredService<IUserSnapshotPublisherService>();
        var recovery = services.GetRequiredService<IUserDataRecoveryCoordinator>();
        var initial = await publisher.GetOrCreateAsync((await users.GetByIdWithRelationsAsync(userId))!);
        var before = await SqliteStorageMeasurement.CaptureAsync(host);
        before.WriteTo(TestContext, "unchanged-before");
        for (var index = 0; index < 24; index++)
        {
            var snapshot = await publisher.GetOrCreateAsync((await users.GetByIdWithRelationsAsync(userId))!);
            Assert.AreEqual(initial.OriginRevision, snapshot.OriginRevision);
            CollectionAssert.AreEqual(initial.SnapshotHash, snapshot.SnapshotHash);
            Assert.IsFalse((await services.GetRequiredService<ISyncRuntimeEligibilityService>().EvaluateAsync()).IsEligible);
            Assert.AreEqual(UserDataRecoveryState.NothingToRecover, (await recovery.TryRecoverAsync(userId, key,
                UserSyncKeyConfidence.AuthenticatedSession, UserDataRecoveryTrigger.ManualRetry)).State);
        }
        var after = await SqliteStorageMeasurement.CaptureAsync(host);
        after.WriteTo(TestContext, "unchanged-after-24");
        before.AssertSameRows(after);
        var checkpoint = await SqliteStorageMeasurement.CheckpointAsync(host);
        Assert.AreEqual(0L, checkpoint.Busy);
        (await SqliteStorageMeasurement.CaptureAsync(host)).WriteTo(TestContext, "unchanged-after-passive-checkpoint");
    }

    [TestMethod]
    public async Task BoundedLiveContent_EditDeleteAndCausalCollection_RecordsStorageWithoutAnArtificialSizeLimit()
    {
        await using var host = await ProductionSyncTestHost.CreateAsync();
        var token = await host.Endpoints.RegisterAsync(host.CreateRegistrationRequest("growth-churn"));
        await host.Endpoints.GetSavedPasswordsAsync(token);
        var userId = (await host.GetOnlyLoginIdentityAsync()).UserId;
        (await SqliteStorageMeasurement.CaptureAsync(host)).WriteTo(TestContext, "churn-before");
        for (var round = 0; round < 12; round++)
        {
            await host.Endpoints.AddNewPasswordAsync(token, new NewPasswordRequest
            {
                Name = "temporary-baseline-entry", Password = Encoding.UTF8.GetBytes("baseline-test-secret")
            });
            var entry = (await host.Endpoints.GetSavedPasswordsAsync(token)).Passwords.Single();
            await host.Endpoints.UpdatePasswordAsync(token, new UpdatePasswordRequest { Id = entry.Id, Description = $"iteration-{round}" });
            await host.Endpoints.RemovePasswordsAsync(token, [entry.Id]);
            using var scope = host.Services.CreateScope();
            using var key = scope.ServiceProvider.GetRequiredService<IUserSessionService>().GetEncryptionKeyFromToken(token);
            var users = scope.ServiceProvider.GetRequiredService<IUserRepository>();
            await scope.ServiceProvider.GetRequiredService<IUserSnapshotPublisherService>()
                .GetOrCreateAsync((await users.GetByIdWithRelationsAsync(userId))!);
            var collection = await scope.ServiceProvider.GetRequiredService<IUserTombstoneGarbageCollector>().CollectAsync(userId, key);
            Assert.IsNull(collection.GlobalBlockReason);
            TestContext.WriteLine($"Causal collection round {round + 1}: examined={collection.ExaminedCount}, removed={collection.RemovedCount}, retained={collection.RetainedCount}");
            using (var bundle = await scope.ServiceProvider.GetRequiredService<IUserDataReaderService>()
                .GetAndVerifyUserDataBundleAsync((await users.GetByIdWithRelationsAsync(userId))!, key))
                Assert.AreEqual(0, bundle.UserPasswordsData.DeletedPasswords.Count,
                    "This local-only workload must collect stable password tombstones through the causal proof path.");
            Assert.AreEqual(0, (await host.Endpoints.GetSavedPasswordsAsync(token)).Passwords.Count);
            if (round % 4 == 3)
                (await SqliteStorageMeasurement.CaptureAsync(host)).WriteTo(TestContext, $"churn-after-{round + 1}");
        }
        var final = await SqliteStorageMeasurement.CaptureAsync(host);
        Assert.AreEqual(1L, final.Rows["Users"]);
        Assert.IsTrue(final.Rows["UserSyncSnapshots"] <= 1, "One unchanged installation retains one latest local snapshot row.");
        var checkpoint = await SqliteStorageMeasurement.CheckpointAsync(host);
        TestContext.WriteLine($"Checkpoint: busy={checkpoint.Busy}, log={checkpoint.LogFrames}, checkpointed={checkpoint.CheckpointedFrames}");
        (await SqliteStorageMeasurement.CaptureAsync(host)).WriteTo(TestContext, "churn-after-passive-checkpoint");
    }

    [TestMethod]
    public async Task RepeatedBackendRestart_PreservesAuthorityWithoutDuplicatingHistory()
    {
        var host = await ProductionSyncTestHost.CreateAsync();
        try
        {
            var token = await host.Endpoints.RegisterAsync(host.CreateRegistrationRequest("growth-restarts"));
            await host.Endpoints.GetSavedPasswordsAsync(token);
            await host.Endpoints.LogoutAsync(token);
            var device = host.Identity.LocalDeviceId;
            var origin = host.Identity.OriginInstanceId;
            var before = await SqliteStorageMeasurement.CaptureAsync(host);
            before.WriteTo(TestContext, "restarts-before");
            for (var index = 0; index < 4; index++)
            {
                host = await host.RestartAsync();
                Assert.AreEqual(device, host.Identity.LocalDeviceId);
                Assert.AreEqual(origin, host.Identity.OriginInstanceId);
                var after = await SqliteStorageMeasurement.CaptureAsync(host);
                after.WriteTo(TestContext, $"restart-{index + 1}");
                before.AssertSameRows(after, "Users", "LocalDeviceIdentity", "UserMembershipAuthorizations",
                    "UserControlOperations", "DeletedUserBarriers", "UserSyncSnapshots", "RevocationDeliveryReceipts");
            }
        }
        finally { await host.DisposeAsync(); }
    }
}
