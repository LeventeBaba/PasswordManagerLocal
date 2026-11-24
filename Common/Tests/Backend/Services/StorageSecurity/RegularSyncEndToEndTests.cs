using System.Text;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using PasswordManagerLocal.Common.Backend.Abstractions.Persistence;
using PasswordManagerLocal.Common.Backend.Abstractions.Repositories;
using PasswordManagerLocal.Common.Backend.Abstractions.Services;
using PasswordManagerLocal.Common.Backend.Constants;
using PasswordManagerLocal.Common.Backend.Models;
using PasswordManagerLocal.Common.Backend.Persistence;
using PasswordManagerLocal.Common.Backend.Services.Hosted;
using PasswordManagerLocal.Common.Backend.Sync;
using PasswordManagerLocal.Common.Backend.Utils;
using PasswordManagerLocal.Common.Contracts.Errors;
using PasswordManagerLocal.Common.Contracts.Requests;
using PasswordManagerLocal.Common.Tests.TestInfrastructure.Storage;
using static PasswordManagerLocal.Common.Tests.TestInfrastructure.Storage.SqliteStorageTestCommands;

namespace PasswordManagerLocal.Common.Tests.Backend.Services;

public sealed partial class RegularSyncEndToEndTests
{
    [TestMethod]
    [TestCategory("Backend")]
    [TestCategory("Integration")]
    [TestCategory("SecurityIngress")]
    [TestCategory("StorageBudget")]
    [Timeout(ThreeDeviceTestTimeoutMilliseconds)]
    public async Task CriticalIngress_ExhaustedInitialControlWrite_BlocksOrdinaryAccessAcrossRestart_AndReplaysAfterSpaceReturns()
    {
        await using var pair = await CreateEnrolledPairAsync();
        await PauseAutomaticDeliveryAsync(pair);
        var userId = (await pair.Target.GetOnlyLoginIdentityAsync()).UserId;
        var username = (await pair.Target.Endpoints.GetUserProfileInfoAsync(pair.TargetToken)).Username;
        var otherToken = await pair.Target.Endpoints.RegisterAsync(pair.Target.CreateRegistrationRequest("unaffected-ingress-profile"));
        await pair.Target.Endpoints.GetSavedPasswordsAsync(otherToken);
        pair.Source.Transport.Disconnect(); pair.Target.Transport.Disconnect();
        await pair.Source.Endpoints.DeleteUserAccountAsync(pair.SourceToken, Encoding.UTF8.GetBytes("P@ssw0rd12345678"));
        UserControlOperationEnvelope operation;
        using (var sourceScope = pair.Source.Services.CreateScope())
        {
            var row = (await sourceScope.ServiceProvider.GetRequiredService<IUserControlOperationRepository>().ListForUserAsync(userId))
                .Single(row => row.OperationType == UserControlOperationType.AccountDeletion);
            operation = UserControlOperationEnvelopeUtil.Deserialize(row.EnvelopePayload);
        }
        var factory = pair.Target.Services.GetRequiredService<SqliteConnectionFactory>();
        await using (var connection = factory.CreateConnection())
        {
            await connection.OpenAsync();
            var used = await ScalarAsync(connection, "SELECT UsedBytes FROM StorageBudgets WHERE Category='Control';");
            await SetBudgetAsync(connection, "Control", used);
        }
        using (var scope = pair.Target.Services.CreateScope())
            await Assert.ThrowsExactlyAsync<StorageCapacityExceededException>(() => scope.ServiceProvider
                .GetRequiredService<IUserControlOperationInboxService>().StoreAndApplyAsync(operation, pair.Source.Identity.LocalDeviceId));
        Assert.IsTrue(pair.Target.Services.GetRequiredService<ICriticalControlIngressStore>().IsSuspended(userId));
        await Assert.ThrowsExactlyAsync<PendingSecurityOperationException>(() => pair.Target.Endpoints.GetSavedPasswordsAsync(pair.TargetToken));
        await Assert.ThrowsExactlyAsync<PendingSecurityOperationException>(() => pair.Target.Endpoints.AddNewPasswordAsync(pair.TargetToken,
            new NewPasswordRequest { Name = "must-not-commit", Password = Encoding.UTF8.GetBytes("test-only") }));
        // The fenced username is never disclosed through a distinguishable login error.
        await Assert.ThrowsExactlyAsync<UnauthorizedAccessException>(() => pair.Target.Endpoints.LoginAsync(pair.Target.CreateLoginRequest(username)));
        await pair.Target.Endpoints.GetSavedPasswordsAsync(otherToken);
        using (var scope = pair.Target.Services.CreateScope())
        {
            Assert.IsNull(await scope.ServiceProvider.GetRequiredService<IUserControlOperationRepository>().GetByIdAsync(operation.OperationId));
            Assert.IsNull(await scope.ServiceProvider.GetRequiredService<IDeletedUserBarrierRepository>().GetAsync(userId));
            Assert.IsTrue(await scope.ServiceProvider.GetRequiredService<IRevocationDeliveryService>().HasPendingDeliveryAsync());
            Assert.IsFalse(await scope.ServiceProvider.GetRequiredService<ISyncRouteRepository>().IsEligibleAsync(userId, pair.Source.Identity.LocalDeviceId));
            CollectionAssert.Contains((await scope.ServiceProvider.GetRequiredService<ISyncRouteRepository>()
                .ListAllControlEligibleUserIdsAsync(pair.Source.Identity.LocalDeviceId)).ToArray(), userId,
                "Fencing ordinary routes must not disable the historical control path needed for recovery.");
        }
        await using var restarted = await pair.Target.RestartAsync();
        var ingress = restarted.Services.GetRequiredService<ICriticalControlIngressStore>();
        Assert.IsTrue(ingress.IsSuspended(userId));
        await Assert.ThrowsExactlyAsync<UnauthorizedAccessException>(() => restarted.Endpoints.LoginAsync(restarted.CreateLoginRequest(username)));
        var recovery = restarted.Services.GetRequiredService<PendingUserControlOperationRecoveryHostedService>();
        await recovery.StartAsync(); // Capacity is still unavailable; the exact received envelope must remain.
        Assert.IsTrue(ingress.IsSuspended(userId));
        Assert.AreEqual(1, (await ingress.ListKeysAsync()).Count);
        await using (var connection = restarted.Services.GetRequiredService<SqliteConnectionFactory>().CreateConnection())
        {
            await connection.OpenAsync();
            await SetBudgetAsync(connection, "Control", StorageCapacityConstants.ControlBytes);
        }
        await recovery.StartAsync(); // Existing startup recovery boundary, not another background loop.
        Assert.IsFalse(ingress.IsSuspended(userId));
        Assert.AreEqual(0, (await ingress.ListKeysAsync()).Count);
        using (var scope = restarted.Services.CreateScope())
        {
            Assert.IsNull(await scope.ServiceProvider.GetRequiredService<IUserRepository>().GetByIdAsync(userId));
            Assert.IsNotNull(await scope.ServiceProvider.GetRequiredService<IDeletedUserBarrierRepository>().GetAsync(userId));
            Assert.AreEqual(UserControlOperationStatus.Applied,
                (await scope.ServiceProvider.GetRequiredService<IUserControlOperationRepository>().GetByIdAsync(operation.OperationId))!.Status);
            Assert.AreEqual(1, (await scope.ServiceProvider.GetRequiredService<IRevocationReceiptRepository>().ListAsync(operation.OperationId)).Count);
        }
        (await SqliteStorageMeasurement.CaptureAsync(restarted)).WriteTo(TestContext, "recovered-critical-admission");
    }
}
