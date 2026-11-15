using Google.Protobuf;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using NSec.Cryptography;
using PasswordManagerLocal.Common.Backend.Abstractions.Services;
using PasswordManagerLocal.Common.Backend.Constants;
using PasswordManagerLocal.Common.Backend.Exceptions;
using PasswordManagerLocal.Common.Backend.Models;
using PasswordManagerLocal.Common.Backend.Repositories;
using PasswordManagerLocal.Common.Backend.Security;
using PasswordManagerLocal.Common.Backend.Services;
using PasswordManagerLocal.Common.Backend.Sync;
using PasswordManagerLocal.Common.Tests.Fakes;
using PasswordManagerLocal.Common.Tests.TestInfrastructure;
using System.Security.Cryptography;

namespace PasswordManagerLocal.Common.Tests.Backend.Services;

[TestClass]
[TestCategory("Backend")]
[TestCategory("Integration")]
[TestCategory("Security")]
public sealed partial class RevocationReceiptServiceTests
{
    [TestMethod]
    public async Task DeletionReceipt_CommitsWithCleanup_AndSurvivesLogoutDuplicateAndReopen()
    {
        await using var scenario = await Scenario.CreateAsync();
        var result = await scenario.Inbox.StoreAndApplyAsync(scenario.Operation, scenario.Peers[0].LocalDeviceId);
        Assert.AreEqual(UserControlOperationReceiptState.Applied, result.State);
        Assert.IsNull(await scenario.Db.Users.GetByIdAsync(scenario.User.UId));
        Assert.IsNotNull(await scenario.Db.DeletedUserBarriers.GetAsync(scenario.User.UId));
        var stored = (await scenario.Db.RevocationReceipts.ListAsync(scenario.Operation.OperationId)).Single();
        var receipt = RevocationDeliveryReceiptUtil.Deserialize(stored.Payload);
        Assert.AreEqual(RevocationReceiptKind.Applied, receipt.Kind);
        Assert.AreEqual(scenario.Peers[1].OriginInstanceId, receipt.RecipientOriginInstanceId);
        RevocationDeliveryReceiptUtil.Verify(receipt, scenario.Operation, scenario.Members[1]);
        await scenario.Inbox.StoreAndApplyAsync(scenario.Operation, scenario.Peers[0].LocalDeviceId);
        CollectionAssert.AreEqual(stored.ReceiptHash,
            (await scenario.Db.RevocationReceipts.ListAsync(scenario.Operation.OperationId)).Single().ReceiptHash);
        // Simulates process loss after commit and before the network reply.
        using var reopened = scenario.Db.OpenSession();
        var durable = new RevocationReceiptRepository(reopened);
        Assert.AreEqual(1, (await durable.ListAsync(scenario.Operation.OperationId)).Count);
        Assert.IsNotNull(await durable.GetEvidenceAsync(scenario.Operation.OperationId));
        Assert.IsNotNull(await scenario.Db.UserControlOperations.GetByIdAsync(scenario.Operation.OperationId));
    }

    [TestMethod]
    public async Task FailedDeletion_NeverSignsAppliedReceipt_ThenPendingRetryCreatesIt()
    {
        await using var scenario = await Scenario.CreateAsync();
        var signCalls = 0;
        var sign = scenario.Peers[1].SignHandler!;
        scenario.Peers[1].SignHandler = bytes => { signCalls++; return sign(bytes); };
        var failingInbox = scenario.CreateInbox(new FailingCleanup());
        await Assert.ThrowsExactlyAsync<InvalidOperationException>(() =>
            failingInbox.StoreAndApplyAsync(scenario.Operation, scenario.Peers[0].LocalDeviceId));
        Assert.AreEqual(0, signCalls);
        Assert.AreEqual(0, (await scenario.Db.RevocationReceipts.ListAsync(scenario.Operation.OperationId)).Count);
        Assert.IsNull(await scenario.Db.RevocationReceipts.GetEvidenceAsync(scenario.Operation.OperationId));
        Assert.AreEqual(UserControlOperationStatus.StoredPending,
            (await scenario.Db.UserControlOperations.GetByIdAsync(scenario.Operation.OperationId))!.Status);
        Assert.IsNotNull(await scenario.Db.DeletedUserBarriers.GetAsync(scenario.User.UId));
        await scenario.Inbox.TryApplyStoredAsync(scenario.Operation.OperationId);
        Assert.AreEqual(1, (await scenario.Db.RevocationReceipts.ListAsync(scenario.Operation.OperationId)).Count);
    }

    [TestMethod]
    public async Task DeletionWhoseSignedSummaryOmitsLocalMember_AppliesWithoutFabricatingReceipt()
    {
        await using var scenario = await Scenario.CreateAsync();
        scenario.Operation.OperationPayload = UserControlOperationEnvelopeUtil.SerializeAccountDeletionPayload(
            UserControlOperationEnvelopeUtil.CreateAccountDeletionPayload(scenario.User,
                scenario.Members.Where(m => m.DeviceId != scenario.Peers[1].LocalDeviceId).ToArray()));
        UserControlOperationEnvelopeUtil.FillOriginAuthentication(scenario.Operation, scenario.Peers[0]);
        var result = await scenario.Inbox.StoreAndApplyAsync(scenario.Operation, scenario.Peers[0].LocalDeviceId);
        Assert.AreEqual(UserControlOperationReceiptState.Applied, result.State);
        Assert.IsNull(await scenario.Db.Users.GetByIdAsync(scenario.User.UId));
        Assert.IsNotNull(await scenario.Db.DeletedUserBarriers.GetAsync(scenario.User.UId));
        Assert.AreEqual(0, (await scenario.Db.RevocationReceipts.ListAsync(scenario.Operation.OperationId)).Count);
        Assert.IsNotNull(await scenario.Db.RevocationReceipts.GetEvidenceAsync(scenario.Operation.OperationId));
    }

    [TestMethod]
    public async Task ReceiptWriteFailure_RollsBackCleanupAndReceipt_ThenRetriesSafely()
    {
        await using var scenario = await Scenario.CreateAsync();
        using (var command = await scenario.Db.UnitOfWork.CreateCommandAsync("""
            CREATE TRIGGER RejectReceipt BEFORE INSERT ON "RevocationDeliveryReceipts"
            BEGIN SELECT RAISE(ABORT, 'injected-receipt-write-failure'); END;
            """)) await command.ExecuteNonQueryAsync();
        var failed = false;
        try { await scenario.Inbox.StoreAndApplyAsync(scenario.Operation, scenario.Peers[0].LocalDeviceId); }
        catch (Exception) { failed = true; }
        Assert.IsTrue(failed);
        Assert.IsNotNull(await scenario.Db.Users.GetByIdAsync(scenario.User.UId));
        Assert.IsNull(await scenario.Db.RevocationReceipts.GetEvidenceAsync(scenario.Operation.OperationId));
        Assert.AreEqual(0, (await scenario.Db.RevocationReceipts.ListAsync(scenario.Operation.OperationId)).Count);
        Assert.AreEqual(UserControlOperationStatus.StoredPending,
            (await scenario.Db.UserControlOperations.GetByIdAsync(scenario.Operation.OperationId))!.Status);
        using (var command = await scenario.Db.UnitOfWork.CreateCommandAsync("DROP TRIGGER RejectReceipt;"))
            await command.ExecuteNonQueryAsync();
        await scenario.Inbox.TryApplyStoredAsync(scenario.Operation.OperationId);
        Assert.IsNull(await scenario.Db.Users.GetByIdAsync(scenario.User.UId));
        Assert.AreEqual(1, (await scenario.Db.RevocationReceipts.ListAsync(scenario.Operation.OperationId)).Count);
    }

    [TestMethod]
    public async Task ThreeDeviceRelay_VerifiesOriginalRecipient_AndDoesNotInventOfflineReceipts()
    {
        await using var scenario = await Scenario.CreateAsync(peerCount: 5);
        await scenario.Inbox.StoreAndApplyAsync(scenario.Operation, scenario.Peers[0].LocalDeviceId);
        var allowed = new HashSet<Guid> { scenario.Operation.OperationId };
        // C signs; B forwards unchanged. Transport identity is deliberately absent from the receipt.
        var fromC = RevocationDeliveryReceiptUtil.Create(scenario.Operation, scenario.Members[2],
            RevocationReceiptKind.Applied, scenario.Peers[2]);
        var payload = RevocationDeliveryReceiptUtil.Serialize(fromC);
        using var authorSession = scenario.Db.OpenSession();
        var atAuthor = scenario.ReceiptsFor(authorSession, scenario.Peers[0]);
        await atAuthor.StoreAsync([payload], allowed);
        await atAuthor.StoreAsync([payload], allowed); // lost ACK and resend
        var rows = await scenario.Db.RevocationReceipts.ListAsync(scenario.Operation.OperationId);
        Assert.AreEqual(2, rows.Count);
        Assert.IsFalse(rows.Any(row => row.RecipientDeviceId == scenario.Peers[3].LocalDeviceId));
        Assert.IsFalse(rows.Any(row => row.RecipientDeviceId == scenario.Peers[4].LocalDeviceId));
        CollectionAssert.AreEqual(payload, rows.Single(row => row.RecipientDeviceId == scenario.Peers[2].LocalDeviceId).Payload);
        // Offline recipients return in reverse order; duplicates do not affect the evidence set.
        foreach (var index in new[] { 4, 3, 0 })
        {
            var returning = RevocationDeliveryReceiptUtil.Create(scenario.Operation, scenario.Members[index],
                RevocationReceiptKind.Applied, scenario.Peers[index]);
            await atAuthor.StoreAsync([RevocationDeliveryReceiptUtil.Serialize(returning)], allowed);
        }
        Assert.AreEqual(5, (await atAuthor.GetInventoryAsync(scenario.Operation.OperationId)).Count);
        // Receipt count alone never proves completion; signed membership and custody are also required.
        Assert.IsTrue(await scenario.Db.UserControlOperations.HasAppliedRevocationAsync());
    }

    [TestMethod]
    public async Task Import_RejectsForgedAndReplayedReceiptsWithoutChangingProgress()
    {
        await using var scenario = await Scenario.CreateAsync();
        await scenario.Inbox.StoreAndApplyAsync(scenario.Operation, scenario.Peers[0].LocalDeviceId);
        var allowed = new HashSet<Guid> { scenario.Operation.OperationId };
        var original = RevocationDeliveryReceiptUtil.Create(scenario.Operation, scenario.Members[0],
            RevocationReceiptKind.Applied, scenario.Peers[0]);
        var mutations = new Action<RevocationDeliveryReceipt>[]
        {
            r => r.Signature[0] ^= 1, r => r.OperationHash[0] ^= 1, r => r.UserId = Guid.NewGuid(),
            r => r.OperationId = Guid.NewGuid(), r => r.RecipientDeviceId = scenario.Peers[1].LocalDeviceId,
            r => r.RecipientOriginInstanceId = Guid.NewGuid(), r => r.RecipientAuthorizationId = Guid.NewGuid(),
            r => r.SignPublicKey[0] ^= 1, r => r.KeyEpoch++, r => r.MembershipEpoch++,
            r => r.Kind = RevocationReceiptKind.DurablyStored, r => r.OperationType = UserControlOperationType.DeviceRemoval
        };
        foreach (var mutate in mutations)
        {
            var forged = RevocationDeliveryReceiptUtil.Deserialize(RevocationDeliveryReceiptUtil.Serialize(original));
            mutate(forged);
            var rejected = false;
            try { await scenario.Receipts.StoreAsync([RevocationDeliveryReceiptUtil.Serialize(forged)], allowed); }
            catch (Exception ex) when (ex is InvalidDataException or UnauthorizedAccessException) { rejected = true; }
            Assert.IsTrue(rejected, "A modified signed field must not be accepted.");
            Assert.AreEqual(1, (await scenario.Db.RevocationReceipts.ListAsync(scenario.Operation.OperationId)).Count);
        }
        Assert.ThrowsExactly<UnauthorizedAccessException>(() => RevocationDeliveryReceiptUtil.Create(
            scenario.Operation, scenario.Members[0], RevocationReceiptKind.Applied, scenario.Peers[1]));
    }

    [TestMethod]
    public async Task StoredReceipt_RemainsDistinctFromApplied_AndUnknownOperationWaitsForApplication()
    {
        await using var scenario = await Scenario.CreateAsync();
        var receipt = RevocationDeliveryReceiptUtil.Create(scenario.Operation, scenario.Members[0],
            RevocationReceiptKind.DurablyStored, scenario.Peers[0]);
        var allowed = new HashSet<Guid> { scenario.Operation.OperationId };
        await Assert.ThrowsExactlyAsync<SyncStateUnavailableException>(() => scenario.Receipts.StoreAsync(
            [RevocationDeliveryReceiptUtil.Serialize(receipt)], allowed));
        await scenario.Inbox.StoreAndApplyAsync(scenario.Operation, scenario.Peers[0].LocalDeviceId);
        await scenario.Receipts.StoreAsync([RevocationDeliveryReceiptUtil.Serialize(receipt)], allowed);
        var rows = await scenario.Db.RevocationReceipts.ListAsync(scenario.Operation.OperationId);
        Assert.AreEqual(1, rows.Count(r => r.Kind == RevocationReceiptKind.DurablyStored));
        Assert.IsFalse(rows.Any(r => r.RecipientDeviceId == scenario.Peers[0].LocalDeviceId && r.Kind == RevocationReceiptKind.Applied));
        var applied = RevocationDeliveryReceiptUtil.Create(scenario.Operation, scenario.Members[0],
            RevocationReceiptKind.Applied, scenario.Peers[0]);
        await scenario.Receipts.StoreAsync([RevocationDeliveryReceiptUtil.Serialize(applied)], allowed);
        Assert.AreEqual(3, (await scenario.Receipts.GetInventoryAsync(scenario.Operation.OperationId)).Count);
    }

    [TestMethod]
    public async Task HistoryReplacement_PreservesEvidence_WithoutAdvertisingOldNamespaceOrReassigningReceipts()
    {
        await using var scenario = await Scenario.CreateAsync();
        await scenario.Inbox.StoreAndApplyAsync(scenario.Operation, scenario.Peers[0].LocalDeviceId);
        var before = (await scenario.Db.RevocationReceipts.ListAsync(scenario.Operation.OperationId)).Single();
        // Exercise the same live-table deletion boundary used by confirmed history reset.
        scenario.Db.UserControlOperations.DeleteRange(await scenario.Db.UserControlOperations.ListForUserAsync(scenario.User.UId));
        scenario.Db.UserMembershipAuthorizations.DeleteRange(await scenario.Db.UserMembershipAuthorizations.ListForUserAsync(scenario.User.UId));
        await scenario.Db.UnitOfWork.SaveChangesAsync();
        Assert.IsNotNull(await scenario.Db.RevocationReceipts.GetEvidenceAsync(scenario.Operation.OperationId));
        CollectionAssert.AreEqual(before.Payload, (await scenario.Db.RevocationReceipts.ListAsync(scenario.Operation.OperationId)).Single().Payload);
        Assert.AreEqual(0, (await scenario.Receipts.GetInventoryAsync(scenario.Operation.OperationId)).Count);
        var newInstallation = RevocationDeliveryReceiptUtil.Deserialize(before.Payload);
        newInstallation.RecipientOriginInstanceId = Guid.NewGuid();
        await Assert.ThrowsExactlyAsync<InvalidDataException>(() => scenario.Receipts.StoreAsync(
            [RevocationDeliveryReceiptUtil.Serialize(newInstallation)], new HashSet<Guid> { scenario.Operation.OperationId }));
        // The frozen old signing identity still verifies old evidence after live history is gone.
        var replacementGenesis = RevocationDeliveryReceiptUtil.Deserialize(before.Payload);
        replacementGenesis.RecipientAuthorizationId = Guid.NewGuid();
        replacementGenesis.Signature = scenario.Peers[1].Sign(RevocationDeliveryReceiptUtil.SigningBytes(replacementGenesis));
        await Assert.ThrowsExactlyAsync<InvalidDataException>(() => scenario.Receipts.StoreAsync(
            [RevocationDeliveryReceiptUtil.Serialize(replacementGenesis)], new HashSet<Guid> { scenario.Operation.OperationId }));
        var oldReceipt = RevocationDeliveryReceiptUtil.Create(scenario.Operation, scenario.Members[0],
            RevocationReceiptKind.Applied, scenario.Peers[0]);
        await scenario.Receipts.StoreAsync([RevocationDeliveryReceiptUtil.Serialize(oldReceipt)],
            new HashSet<Guid> { scenario.Operation.OperationId });
        Assert.IsNotNull(await scenario.Db.DeletedUserBarriers.GetAsync(scenario.User.UId));
    }

    [TestMethod]
    public async Task InstallationRetiredBeforeRevocation_CannotProduceAnApplicationReceipt()
    {
        await using var scenario = await Scenario.CreateAsync();
        var retired = (await scenario.Db.UserMembershipAuthorizations.GetByIdAsync(scenario.Members[1].AuthorizationId))!;
        retired.IsActive = false; retired.EndedMembershipEpoch = 3; retired.MaximumKeyEpoch = 1;
        scenario.Db.UserMembershipAuthorizations.Update(retired);
        scenario.Members[1] = retired;
        scenario.User.MembershipEpoch = 3; scenario.User.GenerateIntegrityHash();
        scenario.Db.Users.Update(scenario.User);
        await scenario.Db.UnitOfWork.SaveChangesAsync();
        scenario.Operation.PreviousMembershipEpoch = 3; scenario.Operation.ResultingMembershipEpoch = 3;
        scenario.Operation.OperationPayload = UserControlOperationEnvelopeUtil.SerializeAccountDeletionPayload(
            UserControlOperationEnvelopeUtil.CreateAccountDeletionPayload(scenario.User, scenario.Members));
        UserControlOperationEnvelopeUtil.FillOriginAuthentication(scenario.Operation, scenario.Peers[0]);
        var result = await scenario.Inbox.StoreAndApplyAsync(scenario.Operation, scenario.Peers[0].LocalDeviceId);
        Assert.AreEqual(UserControlOperationReceiptState.Applied, result.State);
        Assert.AreEqual(0, (await scenario.Db.RevocationReceipts.ListAsync(scenario.Operation.OperationId)).Count);
        Assert.ThrowsExactly<InvalidDataException>(() => RevocationDeliveryReceiptUtil.Create(
            scenario.Operation, retired, RevocationReceiptKind.Applied, scenario.Peers[1]));
    }

    [TestMethod]
    public async Task Quarantine_DoesNotAdvertisePreviouslyAppliedReceipts()
    {
        await using var scenario = await Scenario.CreateAsync();
        await scenario.Inbox.StoreAndApplyAsync(scenario.Operation, scenario.Peers[0].LocalDeviceId);
        var row = (await scenario.Db.UserControlOperations.GetByIdAsync(scenario.Operation.OperationId))!;
        row.Status = UserControlOperationStatus.Quarantined;
        scenario.Db.UserControlOperations.Update(row);
        await scenario.Db.UnitOfWork.SaveChangesAsync();
        Assert.AreEqual(0, (await scenario.Receipts.GetInventoryAsync(row.OperationId)).Count);
        Assert.AreEqual(1, (await scenario.Db.RevocationReceipts.ListAsync(row.OperationId)).Count);
    }

    [TestMethod]
    public async Task RemovedTarget_CanSignOnlyForItsExactHistoricalInstallation()
    {
        await using var scenario = await Scenario.CreateAsync();
        var recipient = scenario.Members[1];
        var removed = scenario.Peers[1];
        var payload = new DeviceRemovalPayload
        {
            UserId = scenario.User.UId, RemovedDeviceId = removed.LocalDeviceId, KeyEpoch = 1,
            PreviousMembershipEpoch = 2, ResultingMembershipEpoch = 3,
            Origins = [new DeviceRemovalOriginCutoffPayload
            {
                AuthorizationId = recipient.AuthorizationId, OriginInstanceId = recipient.OriginInstanceId,
                UserKeyEpoch = 1, HighestAcceptedControlSequence = 0, HighestAcceptedSnapshotRevision = 0,
                SignPublicKeyHash = recipient.SignPublicKeyHash, AdditionOperationId = recipient.AdditionOperationId,
                AdditionOperationHash = recipient.AdditionOperationHash
            }]
        };
        UserControlOperationEnvelopeUtil.FinalizeDeviceRemovalPayload(payload);
        var operation = scenario.Operation;
        operation.OperationType = UserControlOperationType.DeviceRemoval;
        operation.ResultingMembershipEpoch = 3;
        operation.OperationPayload = UserControlOperationEnvelopeUtil.SerializeDeviceRemovalPayload(payload);
        UserControlOperationEnvelopeUtil.FillOriginAuthentication(operation, scenario.Peers[0]);
        await scenario.Inbox.StoreAndApplyAsync(operation, scenario.Peers[0].LocalDeviceId);
        var auth = (await scenario.Db.UserMembershipAuthorizations.GetByIdAsync(recipient.AuthorizationId))!;
        Assert.IsFalse(auth.IsActive);
        Assert.AreEqual(3L, auth.EndedMembershipEpoch);
        var row = (await scenario.Db.RevocationReceipts.ListAsync(operation.OperationId)).Single();
        RevocationDeliveryReceiptUtil.Verify(RevocationDeliveryReceiptUtil.Deserialize(row.Payload), operation, auth);
        // Receipt authority at the removal's base epoch does not authorize post-cutoff control traffic.
        var later = new UserControlOperationEnvelope
        {
            OperationId = Guid.NewGuid(), UserId = scenario.User.UId, OperationType = UserControlOperationType.AccountDeletion,
            OriginDeviceId = removed.LocalDeviceId, OriginInstanceId = removed.OriginInstanceId, OriginSequence = 1,
            PreviousKeyEpoch = 1, ResultingKeyEpoch = 1, PreviousMembershipEpoch = 2, ResultingMembershipEpoch = 2,
            CreatedAtUtc = DateTimeOffset.UtcNow, OperationPayload = scenario.DeletionPayload
        };
        UserControlOperationEnvelopeUtil.FillOriginAuthentication(later, removed);
        await Assert.ThrowsExactlyAsync<UnauthorizedAccessException>(() => scenario.Membership.VerifyControlAuthorAsync(later));
    }

    [TestMethod]
    public async Task ReceiptExchange_RequestsOnlyUnknownEvidence_AndConvergesWithoutResendingKnownPayloads()
    {
        await using var scenario = await Scenario.CreateAsync();
        await scenario.Inbox.StoreAndApplyAsync(scenario.Operation, scenario.Peers[0].LocalDeviceId);
        var fromA = RevocationDeliveryReceiptUtil.Create(scenario.Operation, scenario.Members[0], RevocationReceiptKind.Applied, scenario.Peers[0]);
        var inventory = await scenario.Receipts.GetInventoryAsync(scenario.Operation.OperationId);
        var localUser = UserInventory(scenario, inventory);
        var remoteUser = UserInventory(scenario, [RevocationReceiptService.ToInventory(fromA)]);
        var missing = UserControlOperationAntiEntropyService.FindMissingReceipts([localUser], [remoteUser]);
        Assert.AreEqual(1, missing.Count);
        var allowed = new HashSet<Guid> { scenario.Operation.OperationId };
        await scenario.Receipts.StoreAsync([RevocationDeliveryReceiptUtil.Serialize(fromA)], allowed);
        localUser = UserInventory(scenario, await scenario.Receipts.GetInventoryAsync(scenario.Operation.OperationId));
        Assert.AreEqual(0, UserControlOperationAntiEntropyService.FindMissingReceipts([localUser], [remoteUser]).Count);
        var payloads = await scenario.Receipts.BuildRequestedAsync(missing, allowed);
        Assert.AreEqual(1, payloads.Count);
        CollectionAssert.AreEqual(RevocationDeliveryReceiptUtil.Serialize(fromA), payloads[0]);
        var conflicting = remoteUser.Clone(); conflicting.Receipts[0].ReceiptHash = ByteString.CopyFrom(new byte[32]);
        Assert.ThrowsExactly<InvalidDataException>(() => UserControlOperationAntiEntropyService.FindMissingReceipts([localUser], [conflicting]));
        remoteUser.UserId = Guid.NewGuid().ToString("N");
        Assert.ThrowsExactly<InvalidDataException>(() => UserControlOperationAntiEntropyService.FindMissingReceipts([localUser], [remoteUser]));
        await Assert.ThrowsExactlyAsync<UnauthorizedAccessException>(() => scenario.Receipts.BuildRequestedAsync(missing, new HashSet<Guid>()));
    }

    [TestMethod]
    public async Task ReceiptExchange_BoundsBatches_AndRejectsUnsolicitedOrMissingResponses()
    {
        await using var scenario = await Scenario.CreateAsync();
        await scenario.Inbox.StoreAndApplyAsync(scenario.Operation, scenario.Peers[0].LocalDeviceId);
        var receipts = await scenario.Receipts.GetInventoryAsync(scenario.Operation.OperationId);
        var anti = new UserControlOperationAntiEntropyService(null!, null!, null!, null!, null!);
        var request = new UserControlOperationInventoryExchangeRequest();
        request.RequestedReceipts.Add(receipts[0]);
        await Assert.ThrowsExactlyAsync<SyncStateUnavailableException>(() => anti.AcceptReceiptReplyAsync(
            scenario.Peers[0].LocalDeviceId, request, new UserControlOperationInventoryExchangeReply()));
        var unsolicited = new UserControlOperationInventoryExchangeReply();
        unsolicited.ReceiptResponses.Add(ByteString.CopyFrom((await scenario.Db.RevocationReceipts.ListAsync(scenario.Operation.OperationId))[0].Payload));
        await Assert.ThrowsExactlyAsync<InvalidDataException>(() => anti.AcceptReceiptReplyAsync(
            scenario.Peers[0].LocalDeviceId, new UserControlOperationInventoryExchangeRequest(), unsolicited));
        var excessive = Enumerable.Repeat(receipts[0], SyncConstants.MaxRevocationReceiptsPerCall + 1).ToArray();
        await Assert.ThrowsExactlyAsync<InvalidDataException>(() => scenario.Receipts.BuildRequestedAsync(excessive,
            new HashSet<Guid> { scenario.Operation.OperationId }));
        Assert.ThrowsExactly<InvalidDataException>(() => RevocationDeliveryReceiptUtil.Deserialize(new byte[SyncConstants.MaxRevocationReceiptBytes + 1]));
        Assert.ThrowsExactly<InvalidDataException>(() => RevocationDeliveryReceiptUtil.Deserialize("{}"u8));
        Assert.ThrowsExactly<InvalidDataException>(() => RevocationDeliveryReceiptUtil.Deserialize("null"u8));
        var remote = UserInventory(scenario, []);
        for (var i = 0; i < SyncConstants.MaxRevocationReceiptsPerCall + 1; i++)
        {
            var entry = receipts[0].Clone(); entry.RecipientAuthorizationId = Guid.NewGuid().ToString("N"); remote.Receipts.Add(entry);
        }
        Assert.AreEqual(SyncConstants.MaxRevocationReceiptsPerCall, UserControlOperationAntiEntropyService.FindMissingReceipts(
            [UserInventory(scenario, [])], [remote]).Count);
    }

    [TestMethod]
    public async Task EvidenceStagingRequiresTransaction_AndRollbackNeverLeaksReceipt()
    {
        await using var scenario = await Scenario.CreateAsync();
        await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => scenario.Receipts.StageAppliedAsync(scenario.Operation));
        await using (var transaction = await scenario.Db.UnitOfWork.BeginTransactionAsync())
        {
            await scenario.Receipts.StageAppliedAsync(scenario.Operation);
            await scenario.Db.UnitOfWork.SaveChangesAsync();
            Assert.IsNotNull(await scenario.Db.RevocationReceipts.GetEvidenceAsync(scenario.Operation.OperationId));
            Assert.AreEqual(1, (await scenario.Db.RevocationReceipts.ListAsync(scenario.Operation.OperationId)).Count);
            await transaction.RollbackAsync();
        }
        // This fixture uses SQLite shared-cache memory: a concurrent read of the
        // uncommitted table blocks. Verify independent persisted state after rollback.
        using var otherSession = scenario.Db.OpenSession();
        var persisted = new RevocationReceiptRepository(otherSession);
        Assert.IsNull(await persisted.GetEvidenceAsync(scenario.Operation.OperationId));
        Assert.AreEqual(0, (await persisted.ListAsync(scenario.Operation.OperationId)).Count);
        Assert.IsNull(await scenario.Db.RevocationReceipts.GetEvidenceAsync(scenario.Operation.OperationId));
        Assert.AreEqual(0, (await scenario.Db.RevocationReceipts.ListAsync(scenario.Operation.OperationId)).Count);
    }

    private static UserControlOperationUserInventory UserInventory(Scenario scenario, IEnumerable<RevocationReceiptInventoryEntry> receipts)
    {
        var user = new UserControlOperationUserInventory { UserId = scenario.User.UId.ToString("N") };
        user.Operations.Add(new UserControlOperationInventoryEntry { OperationId = scenario.Operation.OperationId.ToString("N") });
        user.Receipts.AddRange(receipts);
        return user;
    }

    private sealed class FailingCleanup : IUserAccountDeletionCleanupService
    {
        public Task DeleteCanonicalAndPendingStateAsync(Guid userId, CancellationToken ct = default) =>
            throw new InvalidOperationException("injected-cleanup-failure");
    }

    private sealed class Scenario : IAsyncDisposable
    {
        public required SqliteRepositoryTestDatabase Db { get; init; }
        public required Key[] Keys { get; init; }
        public required FakeDeviceIdentityService[] Peers { get; init; }
        public required UserMembershipAuthorization[] Members { get; init; }
        public required User User { get; init; }
        public required UserControlOperationEnvelope Operation { get; init; }
        public required byte[] DeletionPayload { get; init; }
        public UserLifecycleCoordinator Lifecycle { get; } = new();
        public RevocationReceiptService Receipts => ReceiptsFor(Db.UnitOfWork, Peers[1]);
        public UserMembershipAuthorizationService Membership => new(Db.UserMembershipAuthorizations, Db.UserOriginRemovalCutoffs, Peers[1]);
        public UserControlOperationInboxService Inbox => CreateInbox();

        public RevocationReceiptService ReceiptsFor(PasswordManagerLocal.Common.Backend.Persistence.SqliteSession session,
            FakeDeviceIdentityService identity) => new(new RevocationReceiptRepository(session),
                new UserMembershipAuthorizationRepository(session), new UserControlOperationRepository(session), identity, Lifecycle, session);

        public UserControlOperationInboxService CreateInbox(IUserAccountDeletionCleanupService? cleanup = null) => new(
            Db.UserControlOperations, Db.UserControlStates, Db.Users, Db.Devices, Db.UserDevices, Db.UserSyncSnapshots,
            Lifecycle, new FakeInteractiveSessionStateService(new FakeAuthService()), Db.UnitOfWork, Membership,
            Db.UserMembershipAuthorizations, Db.LocalUserDevices, Peers[1], new FakeSyncRuntimeService(), Db.DeletedUserBarriers,
            cleanup ?? new UserAccountDeletionCleanupService(new UserAccountDeletionStore(Db.UnitOfWork, Db.Users, Db.DeviceEnrollmentCommits), Db.Users),
            revocationReceipts: Receipts);

        public static async Task<Scenario> CreateAsync(int peerCount = 3, bool authenticatedHistory = false)
        {
            var database = await SqliteRepositoryTestDatabase.CreateAsync();
            var keys = Enumerable.Range(0, peerCount).Select(_ => Key.Create(SignatureAlgorithm.Ed25519, new KeyCreationParameters())).ToArray();
            var peers = keys.Select(key => new FakeDeviceIdentityService
            {
                LocalDeviceId = Guid.NewGuid(), OriginInstanceId = Guid.NewGuid(),
                SignPublicKey = key.PublicKey.Export(KeyBlobFormat.RawPublicKey), AgreementPublicKey = RandomNumberGenerator.GetBytes(32),
                FingerprintHex = Convert.ToHexString(RandomNumberGenerator.GetBytes(32)),
                SignHandler = bytes => SignatureAlgorithm.Ed25519.Sign(key, bytes)
            }).ToArray();
            var user = new User
            {
                UId = Guid.NewGuid(), UsernameHash = RandomNumberGenerator.GetBytes(32), UsernameSalt = RandomNumberGenerator.GetBytes(32),
                PasswordSalt = [1], EncryptedPayload = [2], EncryptedGeneralUserDataPayload = [3],
                EncryptedUserPasswordsDataPayload = [4], EncryptedUserDevicesDataPayload = [5], SavedKey = RandomNumberGenerator.GetBytes(32),
                KeyEpoch = 1, MembershipEpoch = authenticatedHistory ? peerCount : 2, GeneralDataVersionPhysicalTimeUnixMilliseconds = 1000,
                GeneralDataVersionOriginDeviceId = peers[0].LocalDeviceId, GeneralDataVersionOriginInstanceId = peers[0].OriginInstanceId,
                LastModifiedAt = DateTimeOffset.UtcNow, UserDataLastModifiedAt = DateTimeOffset.UtcNow,
                GeneralUserDataLastModifiedAt = DateTimeOffset.UtcNow, UserPasswordsDataLastModifiedAt = DateTimeOffset.UtcNow,
                UserDevicesDataLastModifiedAt = DateTimeOffset.UtcNow
            };
            user.GenerateIntegrityHash();
            var members = peers.Select((peer, index) =>
            {
                var id = Guid.NewGuid();
                return new UserMembershipAuthorization
                {
                    AuthorizationId = id, UserId = user.UId, DeviceId = peer.LocalDeviceId, OriginInstanceId = peer.OriginInstanceId,
                    SignPublicKey = peer.SignPublicKey, SignPublicKeyHash = Hashing.SHA256Hash(peer.SignPublicKey),
                    AgreementPublicKeyHash = Hashing.SHA256Hash(peer.AgreementPublicKey), TlsCertFingerprint = peer.FingerprintHex,
                    DeviceType = peer.DeviceType, StartedMembershipEpoch = authenticatedHistory ? index + 1 : index == 0 ? 1 : 2, MinimumKeyEpoch = 1,
                    IsActive = true, IsGenesis = index == 0, AdditionOperationId = index == 0 ? null : id,
                    AdditionOperationHash = index == 0 ? null : RandomNumberGenerator.GetBytes(32)
                };
            }).ToArray();
            var historicalOperations = new List<UserControlOperationEnvelope>();
            if (authenticatedHistory)
            {
                for (var index = 1; index < peerCount; index++)
                {
                    var peer = peers[index];
                    var addition = new UserControlOperationEnvelope
                    {
                        OperationId = members[index].AuthorizationId, UserId = user.UId, OperationType = UserControlOperationType.DeviceAddition,
                        OriginDeviceId = peers[0].LocalDeviceId, OriginInstanceId = peers[0].OriginInstanceId, OriginSequence = index,
                        PreviousKeyEpoch = 1, ResultingKeyEpoch = 1, PreviousMembershipEpoch = index, ResultingMembershipEpoch = index + 1,
                        CreatedAtUtc = DateTimeOffset.UtcNow,
                        OperationPayload = UserControlOperationEnvelopeUtil.SerializeDeviceAdditionPayload(
                            UserControlOperationEnvelopeUtil.CreateDeviceAdditionPayload(user.UId, 1, index, peer.LocalDeviceId,
                                peer.OriginInstanceId, peer.SignPublicKey, peer.AgreementPublicKey, peer.FingerprintHex, peer.DeviceType))
                    };
                    UserControlOperationEnvelopeUtil.FillOriginAuthentication(addition, peers[0]);
                    members[index].AdditionOperationHash = addition.OperationHash.ToArray();
                    historicalOperations.Add(addition);
                    await database.UserControlOperations.AddAsync(UserControlOperationMapping.ToStoredOperation(addition,
                        UserControlOperationEnvelopeUtil.Serialize(addition), UserControlOperationStatus.Applied, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow));
                }
                foreach (var peer in peers)
                {
                    var device = new Device { Id = peer.LocalDeviceId, SignPublicKey = peer.SignPublicKey, PublicKey = peer.AgreementPublicKey,
                        TlsCertFingerprint = peer.FingerprintHex, DeviceType = peer.DeviceType, IsTrusted = true };
                    device.GenerateIntegrityHash();
                    await database.Devices.AddAsync(device);
                }
            }
            await database.Users.AddAsync(user);
            foreach (var member in members) await database.UserMembershipAuthorizations.AddAsync(member);
            await database.UnitOfWork.SaveChangesAsync();
            var payload = UserControlOperationEnvelopeUtil.SerializeAccountDeletionPayload(
                UserControlOperationEnvelopeUtil.CreateAccountDeletionPayload(user, members));
            var operation = new UserControlOperationEnvelope
            {
                OperationId = Guid.NewGuid(), UserId = user.UId, OperationType = UserControlOperationType.AccountDeletion,
                OriginDeviceId = peers[0].LocalDeviceId, OriginInstanceId = peers[0].OriginInstanceId, OriginSequence = authenticatedHistory ? peerCount : 1,
                PreviousKeyEpoch = 1, ResultingKeyEpoch = 1, PreviousMembershipEpoch = user.MembershipEpoch, ResultingMembershipEpoch = user.MembershipEpoch,
                RevocationMembers = authenticatedHistory ? UserControlOperationEnvelopeUtil.CreateMembershipSummary(members) : [],
                RevocationHistoryProofs = historicalOperations,
                CreatedAtUtc = DateTimeOffset.UtcNow, OperationPayload = payload
            };
            UserControlOperationEnvelopeUtil.FillOriginAuthentication(operation, peers[0]);
            return new Scenario { Db = database, Keys = keys, Peers = peers, Members = members, User = user, Operation = operation, DeletionPayload = payload };
        }

        public async ValueTask DisposeAsync()
        {
            foreach (var key in Keys) key.Dispose();
            await Db.DisposeAsync();
        }
    }
}
