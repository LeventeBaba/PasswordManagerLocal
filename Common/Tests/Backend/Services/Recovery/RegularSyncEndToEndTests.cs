using Microsoft.Extensions.DependencyInjection;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using PasswordManagerLocal.Common.Backend.Abstractions.Repositories;
using PasswordManagerLocal.Common.Backend.Abstractions.Services;
using PasswordManagerLocal.Common.Backend.Models;
using PasswordManagerLocal.Common.Backend.Sync;
using PasswordManagerLocal.Common.Tests.TestInfrastructure;
using System.Text;

namespace PasswordManagerLocal.Common.Tests.Backend.Services;

public sealed partial class RegularSyncEndToEndTests
{
    [TestMethod]
    [Timeout(ThreeDeviceTestTimeoutMilliseconds)]
    [TestCategory("Recovery")]
    [TestCategory("TransactionHardening")]
    [TestCategory("SyncStressExtended")]
    public async Task RecoveredCanonicalPublication_LockedPeerRestart_PreservesCustodyAndEventuallyMerges()
    {
        if (Environment.GetEnvironmentVariable("PASSWORDMANAGERLOCAL_SYNC_STRESS") != "1")
            Assert.Inconclusive("Set PASSWORDMANAGERLOCAL_SYNC_STRESS=1 for recovery and locked-peer restart validation.");
        await using var pair = await CreateEnrolledPairAsync();
        await pair.Source.Endpoints.GetSavedPasswordsAsync(pair.SourceToken);
        await pair.Target.Endpoints.GetSavedPasswordsAsync(pair.TargetToken);
        pair.Source.Transport.Disconnect();
        pair.Target.Transport.Disconnect();
        await RunStepAsync("drain peers before canonical damage", async ct =>
        {
            await pair.Source.SyncTasks.StopAllAsync(ct);
            await pair.Target.SyncTasks.StopAllAsync(ct);
        });
        const string name = "Recovered private target credential";
        var secret = Encoding.UTF8.GetBytes("recovery-convergence-secret");
        await AddPasswordAsync(pair.Target, pair.TargetToken, name, secret);
        Guid userId;
        string username;
        UserDataRecoveryResult recovered;
        using (var scope = pair.Target.Services.CreateScope())
        {
            var users = scope.ServiceProvider.GetRequiredService<IUserRepository>();
            userId = (await users.ListUserIdsAsync()).Single();
            var user = (await users.GetByIdAsync(userId))!;
            Assert.IsTrue(scope.ServiceProvider.GetRequiredService<IUserSyncKeyResolverService>()
                .TryResolve(user, out var key, out var confidence));
            Assert.IsNotNull(key);
            using (key)
            {
                using (var verification = await scope.ServiceProvider.GetRequiredService<IUserDataBundleVerificationService>()
                           .VerifyCanonicalAsync(user, key, confidence))
                {
                    Assert.IsTrue(verification.IsHealthy);
                    username = verification.VerifiedBundle!.GeneralUserData.Username;
                }
                var damaged = user.EncryptedPayload.ToArray();
                damaged[0] ^= 0x55;
                await scope.ServiceProvider.ExecuteFixtureSqlAsync(
                    "UPDATE Users SET EncryptedPayload = $payload WHERE UId = $user;", ("$payload", damaged), ("$user", userId));
                recovered = await scope.ServiceProvider.GetRequiredService<IUserDataRecoveryCoordinator>()
                    .TryRecoverAsync(userId, key, confidence, UserDataRecoveryTrigger.ManualRetry);
                Assert.AreEqual(UserDataRecoveryState.Recovered, recovered.State);
                var durable = (await users.GetByIdAsNoTrackingAsync(userId))!;
                var checkpoint = (await scope.ServiceProvider.GetRequiredService<IUserCanonicalCheckpointRepository>().GetAsync(userId))!;
                UserCanonicalCheckpointUtil.Verify(durable, checkpoint, pair.Target.Identity);
                Assert.AreEqual(UserLoginIdentityStatus.Active,
                    (await users.GetLoginIdentityStateAsync(userId))!.Status);
                Assert.IsTrue(await scope.ServiceProvider.GetRequiredService<ISyncQueueRepository>()
                    .HasPendingForDeviceAsync(pair.Source.Identity.LocalDeviceId));
            }
        }
        await pair.Source.Endpoints.LogoutAsync(pair.SourceToken);
        pair.Target.ConnectTo(pair.Source);
        pair.Source.ConnectTo(pair.Target);
        await SynchronizeOneWayUntilAsync(pair.Target, pair.Source, "locked peer stores recovered publication", async () =>
        {
            using var scope = pair.Source.Services.CreateScope();
            var knowledge = await scope.ServiceProvider.GetRequiredService<IUserRevisionKnowledgeRepository>()
                .GetAsync(userId, pair.Target.Identity.LocalDeviceId, pair.Target.Identity.OriginInstanceId, 1);
            return knowledge is not null && knowledge.HighestStoredRevision >= recovered.RecoveryRevision;
        });
        pair.Target.Transport.Disconnect();
        pair.Source.Transport.Disconnect();
        await RunStepAsync("drain sessions before locked restart", async ct =>
        {
            await pair.Source.SyncTasks.StopAllAsync(ct);
            await pair.Target.SyncTasks.StopAllAsync(ct);
        });
        await using var restarted = await pair.Source.RestartAsync();
        using (var scope = restarted.Services.CreateScope())
        {
            var knowledge = (await scope.ServiceProvider.GetRequiredService<IUserRevisionKnowledgeRepository>()
                .GetAsync(userId, pair.Target.Identity.LocalDeviceId, pair.Target.Identity.OriginInstanceId, 1))!;
            Assert.IsTrue(knowledge.HighestStoredRevision >= recovered.RecoveryRevision);
            Assert.IsTrue(knowledge.HighestMergedRevision < recovered.RecoveryRevision,
                "A locked recipient must retain custody without acknowledging a canonical merge.");
        }
        var token = await restarted.Endpoints.LoginAsync(restarted.CreateLoginRequest(username));
        await restarted.Endpoints.GetSavedPasswordsAsync(token);
        await RunStepAsync("unlocked restarted peer merges recovered publication", async ct =>
        {
            while (!(await restarted.Endpoints.GetSavedPasswordsAsync(token, ct)).Passwords.Any(item => item.Name == name))
                await Task.Delay(10, ct);
        });
        await AssertPasswordSecretAsync(restarted, token, name, secret);
    }
}
