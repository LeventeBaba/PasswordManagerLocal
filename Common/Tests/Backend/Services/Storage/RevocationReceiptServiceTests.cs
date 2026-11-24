using Microsoft.VisualStudio.TestTools.UnitTesting;
using PasswordManagerLocal.Common.Backend.Constants;
using PasswordManagerLocal.Common.Backend.Models;
using PasswordManagerLocal.Common.Backend.Persistence;
using PasswordManagerLocal.Common.Backend.Repositories;
using PasswordManagerLocal.Common.Backend.Services;
using PasswordManagerLocal.Common.Backend.Sync;
using PasswordManagerLocal.Common.Contracts.Errors;
using PasswordManagerLocal.Common.Tests.Fakes;
using PasswordManagerLocal.Common.Tests.TestInfrastructure.Storage;
using static PasswordManagerLocal.Common.Tests.TestInfrastructure.Storage.SqliteStorageTestCommands;

namespace PasswordManagerLocal.Common.Tests.Backend.Services;

public sealed partial class RevocationReceiptServiceTests
{
    [TestMethod]
    [TestCategory("SecurityIngress")]
    [TestCategory("StorageBudget")]
    public async Task CriticalIngress_ControlBudgetExhaustion_RetainsExactEnvelopeAndFenceAcrossReopen()
    {
        await using var scenario = await Scenario.CreateAsync();
        using var ingress = new CriticalControlIngressStore(scenario.Db.Connections);
        var lifecycle = new UserLifecycleCoordinator(ingress);
        await using var connection = scenario.Db.Connections.CreateConnection();
        await connection.OpenAsync();
        var used = await ScalarAsync(connection, "SELECT UsedBytes FROM StorageBudgets WHERE Category='Control';");
        await SetBudgetAsync(connection, "Control", used);
        var inbox = scenario.CreateInbox(ingress: ingress, lifecycle: lifecycle);
        await Assert.ThrowsExactlyAsync<StorageCapacityExceededException>(() => inbox.StoreAndApplyAsync(scenario.Operation, scenario.Peers[0].LocalDeviceId));
        Assert.IsTrue(ingress.IsSuspended(scenario.User.UId));
        using (var readback = scenario.Db.OpenSession())
        {
            Assert.IsNull(await new UserControlOperationRepository(readback).GetByIdAsync(scenario.Operation.OperationId));
            Assert.IsNull(await new DeletedUserBarrierRepository(readback).GetAsync(scenario.User.UId));
            Assert.IsNotNull(await new UserRepository(readback).GetByIdAsNoTrackingAsync(scenario.User.UId));
            Assert.AreEqual(0, (await new RevocationReceiptRepository(readback).ListAsync(scenario.Operation.OperationId)).Count);
        }
        var key = (await ingress.ListKeysAsync()).Single();
        CollectionAssert.AreEqual(UserControlOperationEnvelopeUtil.Serialize(scenario.Operation), (await ingress.ReadAsync(key))!.EnvelopePayload);
        using var reopened = new CriticalControlIngressStore(scenario.Db.Connections);
        reopened.Initialize();
        var resumedLifecycle = new UserLifecycleCoordinator(reopened);
        await Assert.ThrowsExactlyAsync<PendingSecurityOperationException>(() =>
            resumedLifecycle.ExecuteAsync(scenario.User.UId, _ => Task.CompletedTask));
        var unaffectedRan = false;
        await resumedLifecycle.ExecuteAsync(Guid.NewGuid(), _ => { unaffectedRan = true; return Task.CompletedTask; });
        Assert.IsTrue(unaffectedRan);
        await SetBudgetAsync(connection, "Control", StorageCapacityConstants.ControlBytes);
        var resumed = scenario.CreateInbox(ingress: reopened, lifecycle: resumedLifecycle);
        var result = await resumed.StoreAndApplyAsync(scenario.Operation, scenario.Peers[0].LocalDeviceId);
        Assert.AreEqual(UserControlOperationReceiptState.Applied, result.State);
        Assert.IsFalse(reopened.IsSuspended(scenario.User.UId));
        Assert.AreEqual(0, (await reopened.ListKeysAsync()).Count);
        Assert.IsNotNull(await scenario.Db.DeletedUserBarriers.GetAsync(scenario.User.UId));
        Assert.AreEqual(1, (await scenario.Db.RevocationReceipts.ListAsync(scenario.Operation.OperationId)).Count);
    }

    [TestMethod]
    [TestCategory("SecurityIngress")]
    [TestCategory("StorageBudget")]
    public async Task CriticalIngress_FullIngressBudget_DoesNotVerifyAuthorityOrAcknowledgeCustody()
    {
        await using var scenario = await Scenario.CreateAsync();
        using var ingress = new CriticalControlIngressStore(scenario.Db.Connections);
        var verifier = new FakeUserMembershipAuthorizationService();
        await using var connection = scenario.Db.Connections.CreateConnection();
        await connection.OpenAsync();
        await SetBudgetAsync(connection, "Ingress", 1);
        var inbox = scenario.CreateInbox(ingress: ingress, lifecycle: new UserLifecycleCoordinator(ingress), membership: verifier);
        await Assert.ThrowsExactlyAsync<StorageCapacityExceededException>(() => inbox.StoreAndApplyAsync(scenario.Operation, scenario.Peers[0].LocalDeviceId));
        Assert.AreEqual(0, verifier.ControlAuthorVerificationCalls, "No author authority may be accepted before durable ingress.");
        Assert.AreEqual(0, (await ingress.ListKeysAsync()).Count);
        Assert.IsFalse(ingress.IsSuspended(scenario.User.UId));
        Assert.IsNull(await scenario.Db.UserControlOperations.GetByIdAsync(scenario.Operation.OperationId));
        Assert.AreEqual(0, (await scenario.Db.RevocationReceipts.ListAsync(scenario.Operation.OperationId)).Count);
    }

    [TestMethod]
    [TestCategory("SecurityIngress")]
    [TestCategory("StorageBudget")]
    public async Task CriticalIngress_DuplicatesDoNotGrow_AndCompletionRequiresDurableTerminalProof()
    {
        await using var scenario = await Scenario.CreateAsync();
        var activity = new RevocationDeliveryActivity();
        using var ingress = new CriticalControlIngressStore(scenario.Db.Connections, activity);
        var key = await ingress.RetainAsync(scenario.Operation, scenario.Peers[0].LocalDeviceId);
        await using var connection = scenario.Db.Connections.CreateConnection();
        await connection.OpenAsync();
        var used = await ScalarAsync(connection, "SELECT UsedBytes FROM StorageBudgets WHERE Category='Ingress';");
        var version = activity.EvidenceVersion;
        for (var attempt = 0; attempt < 32; attempt++)
            CollectionAssert.AreEqual(key, await ingress.RetainAsync(scenario.Operation, scenario.Peers[0].LocalDeviceId));
        Assert.AreEqual(used, await ScalarAsync(connection, "SELECT UsedBytes FROM StorageBudgets WHERE Category='Ingress';"));
        Assert.AreEqual(1L, await ScalarAsync(connection, "SELECT COUNT(*) FROM CriticalControlIngress;"));
        Assert.AreEqual(version, activity.EvidenceVersion, "An exact duplicate is not new persistent evidence.");
        await ingress.CompleteAsync(scenario.User.UId, scenario.Operation.OperationId, scenario.Operation.OperationHash);
        Assert.IsTrue(ingress.IsSuspended(scenario.User.UId), "Custody alone cannot remove a security fence.");
        var inbox = scenario.CreateInbox(ingress: ingress, lifecycle: new UserLifecycleCoordinator(ingress));
        Assert.AreEqual(UserControlOperationReceiptState.Applied,
            (await inbox.StoreAndApplyAsync(scenario.Operation, scenario.Peers[0].LocalDeviceId)).State);
        var completedVersion = activity.EvidenceVersion;
        await ingress.RetainAsync(scenario.Operation, scenario.Peers[0].LocalDeviceId);
        Assert.AreEqual(0, (await ingress.ListKeysAsync()).Count, "An exact already-applied core does not create fresh ingress custody.");
        Assert.AreEqual(completedVersion, activity.EvidenceVersion);
    }

    [TestMethod]
    [TestCategory("SecurityIngress")]
    [TestCategory("StorageBudget")]
    public async Task CriticalIngress_InvalidSignatureIsDiscarded_AndDoesNotLeaveAProfileBlocked()
    {
        await using var scenario = await Scenario.CreateAsync();
        using var ingress = new CriticalControlIngressStore(scenario.Db.Connections);
        var invalid = UserControlOperationEnvelopeUtil.Deserialize(UserControlOperationEnvelopeUtil.Serialize(scenario.Operation));
        invalid.OriginSignature[0] ^= 1;
        var inbox = scenario.CreateInbox(ingress: ingress, lifecycle: new UserLifecycleCoordinator(ingress));
        var result = await inbox.StoreAndApplyAsync(invalid, scenario.Peers[0].LocalDeviceId);
        Assert.AreEqual(UserControlOperationReceiptState.Rejected, result.State);
        Assert.IsFalse(ingress.IsSuspended(scenario.User.UId));
        Assert.AreEqual(0, (await ingress.ListKeysAsync()).Count);
        Assert.IsNull(await scenario.Db.DeletedUserBarriers.GetAsync(scenario.User.UId));
    }

    [TestMethod]
    [TestCategory("SecurityIngress")]
    [TestCategory("StorageBudget")]
    public async Task CriticalIngress_PostCommitCleanupFailure_KeepsFenceUntilAnExactRetryConfirmsApplication()
    {
        await using var scenario = await Scenario.CreateAsync();
        using var ingress = new CriticalControlIngressStore(scenario.Db.Connections);
        await using var connection = scenario.Db.Connections.CreateConnection();
        await connection.OpenAsync();
        await ExecuteAsync(connection, """
            CREATE TRIGGER RejectIngressCleanup BEFORE DELETE ON CriticalControlIngress
            BEGIN SELECT RAISE(ABORT, 'pml_capacity:ingress-cleanup-test'); END;
            """);
        var inbox = scenario.CreateInbox(ingress: ingress, lifecycle: new UserLifecycleCoordinator(ingress));
        await Assert.ThrowsExactlyAsync<StorageCapacityExceededException>(() => inbox.StoreAndApplyAsync(scenario.Operation, scenario.Peers[0].LocalDeviceId));
        Assert.IsTrue(ingress.IsSuspended(scenario.User.UId));
        Assert.AreEqual(UserControlOperationStatus.Applied,
            (await scenario.Db.UserControlOperations.GetByIdAsync(scenario.Operation.OperationId))!.Status);
        Assert.IsNotNull(await scenario.Db.DeletedUserBarriers.GetAsync(scenario.User.UId));
        Assert.AreEqual(1, (await scenario.Db.RevocationReceipts.ListAsync(scenario.Operation.OperationId)).Count);
        await ExecuteAsync(connection, "DROP TRIGGER RejectIngressCleanup;");
        Assert.AreEqual(UserControlOperationReceiptState.Applied,
            (await inbox.StoreAndApplyAsync(scenario.Operation, scenario.Peers[0].LocalDeviceId)).State);
        Assert.IsFalse(ingress.IsSuspended(scenario.User.UId));
        Assert.AreEqual(1, (await scenario.Db.RevocationReceipts.ListAsync(scenario.Operation.OperationId)).Count);
    }

    [TestMethod]
    [TestCategory("SecurityIngress")]
    [TestCategory("StorageBudget")]
    public async Task CriticalIngress_ControlScopeDoesNotEscape_AndNewAuthorityCannotOvertakeRetainedRevocation()
    {
        await using var scenario = await Scenario.CreateAsync();
        using var ingress = new CriticalControlIngressStore(scenario.Db.Connections);
        await ingress.RetainAsync(scenario.Operation, scenario.Peers[0].LocalDeviceId);
        var candidate = new UserControlOperationEnvelope
        {
            UserId = scenario.User.UId, ResultingKeyEpoch = scenario.Operation.PreviousKeyEpoch + 1,
            ResultingMembershipEpoch = scenario.Operation.PreviousMembershipEpoch
        };
        Assert.IsFalse(await ingress.CanApplyPredecessorAsync(candidate));
        candidate.ResultingKeyEpoch = scenario.Operation.PreviousKeyEpoch;
        Assert.IsTrue(await ingress.CanApplyPredecessorAsync(candidate), "This additional bound must allow genuine historical prerequisites; it does not grant signature authority.");
        var lifecycle = new UserLifecycleCoordinator(ingress);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Task? escaped = null;
        await lifecycle.ExecuteControlAsync(scenario.User.UId, async ct =>
        {
            Assert.IsTrue(lifecycle.IsControlOperation(scenario.User.UId));
            await lifecycle.ExecuteAsync(scenario.User.UId, _ => Task.CompletedTask, ct);
            escaped = Task.Run(async () =>
            {
                await release.Task;
                await lifecycle.ExecuteAsync(scenario.User.UId, _ => Task.CompletedTask);
            });
            return true;
        });
        Assert.IsFalse(lifecycle.IsControlOperation(scenario.User.UId));
        release.SetResult();
        await Assert.ThrowsExactlyAsync<PendingSecurityOperationException>(() => escaped!);
        Assert.IsTrue(ingress.IsSuspended(scenario.User.UId));
    }
}
