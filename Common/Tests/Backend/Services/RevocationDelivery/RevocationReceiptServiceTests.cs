using Microsoft.Extensions.DependencyInjection;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using PasswordManagerLocal.Common.Backend.Abstractions.Repositories;
using PasswordManagerLocal.Common.Backend.Abstractions.Services;
using PasswordManagerLocal.Common.Backend.Models;
using PasswordManagerLocal.Common.Backend.Services;
using PasswordManagerLocal.Common.Backend.State;
using PasswordManagerLocal.Common.Backend.Sync;
using PasswordManagerLocal.Common.Backend.Sync.Discovery;
using PasswordManagerLocal.Common.Tests.Fakes;
using PasswordManagerLocal.Common.Tests.TestInfrastructure;

namespace PasswordManagerLocal.Common.Tests.Backend.Services;

public sealed partial class RevocationReceiptServiceTests
{
    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task AccountDeletionWire_AbsentOptionalHashesRoundTrip_WithoutRejectingApplication(bool emptyHashes)
    {
        await using var scenario = await Scenario.CreateAsync(peerCount: 2, authenticatedHistory: true);
        var original = UserControlOperationEnvelopeUtil.DeserializeAccountDeletionPayload(scenario.Operation.OperationPayload);
        var json = System.Text.Json.Nodes.JsonNode.Parse(scenario.Operation.OperationPayload)!;
        foreach (var member in json["KnownMembers"]!.AsArray())
        {
            if (member!["AdditionOperationId"] is null) member["AdditionOperationHash"] = emptyHashes ? "" : null;
            member["RemovalOperationHash"] = emptyHashes ? "" : null;
        }
        var decoded = UserControlOperationEnvelopeUtil.DeserializeAccountDeletionPayload(System.Text.Encoding.UTF8.GetBytes(json.ToJsonString()));
        CollectionAssert.AreEqual(original.MembershipHistoryHash, decoded.MembershipHistoryHash);
        CollectionAssert.AreEqual(original.IntegrityHash, decoded.IntegrityHash);
        Assert.IsNull(decoded.KnownMembers.Single(m => m.StartedMembershipEpoch == 1).AdditionOperationHash);
        Assert.IsTrue(decoded.KnownMembers.All(m => m.RemovalOperationHash is null));
        CollectionAssert.AreEqual(scenario.Members[1].AdditionOperationHash!, decoded.KnownMembers.Single(m => m.StartedMembershipEpoch == 2).AdditionOperationHash!);
        scenario.Operation.OperationPayload = UserControlOperationEnvelopeUtil.SerializeAccountDeletionPayload(decoded);
        UserControlOperationEnvelopeUtil.FillOriginAuthentication(scenario.Operation, scenario.Peers[0]);
        var envelope = UserControlOperationEnvelopeUtil.Deserialize(UserControlOperationEnvelopeUtil.Serialize(scenario.Operation));
        UserControlOperationEnvelopeUtil.VerifyWithSigningKey(envelope, scenario.Peers[0].SignPublicKey);
        await using var recipient = DeliveryNode.Wrap(scenario);
        Assert.AreEqual(UserControlOperationReceiptState.Applied, (await recipient.ApplyAsync(envelope)).State);
        Assert.IsNotNull(await recipient.Db.DeletedUserBarriers.GetAsync(scenario.User.UId));
        Assert.AreNotEqual(RevocationDeliveryState.Blocked, (await SnapshotAsync(recipient, envelope.OperationId)).State);
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task RemovalWire_AbsentManifestHashesRoundTrip_WithOriginalSignatureAndHistoryIntact(bool emptyHashes)
    {
        await using var scenario = await Scenario.CreateAsync(peerCount: 2, authenticatedHistory: true);
        var removal = CreateRemoval(scenario, scenario.Operation, removedIndex: 1);
        var json = System.Text.Json.Nodes.JsonNode.Parse(UserControlOperationEnvelopeUtil.Serialize(removal))!;
        foreach (var member in json["RevocationMembers"]!.AsArray())
        {
            if (member!["AdditionOperationId"] is null) member["AdditionOperationHash"] = emptyHashes ? "" : null;
            member["RemovalOperationHash"] = emptyHashes ? "" : null;
        }
        var decoded = UserControlOperationEnvelopeUtil.Deserialize(System.Text.Encoding.UTF8.GetBytes(json.ToJsonString()));
        UserControlOperationEnvelopeUtil.VerifyWithSigningKey(decoded, scenario.Peers[0].SignPublicKey);
        CollectionAssert.AreEqual(removal.OperationHash, decoded.OperationHash);
        CollectionAssert.AreEqual(removal.OriginSignature, decoded.OriginSignature);
        Assert.IsNull(decoded.RevocationMembers.Single(m => m.StartedMembershipEpoch == 1).AdditionOperationHash);
        Assert.IsTrue(decoded.RevocationMembers.All(m => m.RemovalOperationHash is null));
        Assert.AreEqual(1, decoded.RevocationHistoryProofs.Count);
        UserControlOperationEnvelopeUtil.VerifyWithSigningKey(decoded.RevocationHistoryProofs.Single(), scenario.Peers[0].SignPublicKey);
        await using var author = await DeliveryNode.CreateAsync(scenario, 0);
        Assert.AreEqual(UserControlOperationReceiptState.Applied, (await author.ApplyAsync(decoded)).State);
        Assert.AreEqual(RevocationDeliveryState.Completed, (await SnapshotAsync(author, decoded.OperationId)).State);
        var ended = (await author.Db.UserMembershipAuthorizations.GetByIdAsync(scenario.Members[1].AuthorizationId))!;
        CollectionAssert.AreEqual(decoded.OperationHash, ended.RemovalOperationHash!);
        Assert.AreEqual(1, (await author.Db.UserOriginRemovalCutoffs.ListForUserAsync(scenario.User.UId)).Count);
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task GenesisRemovalWire_AbsentAdditionHashRoundTrips_WithoutLosingSignedCutoffs(bool emptyHash)
    {
        await using var scenario = await Scenario.CreateAsync(peerCount: 2, authenticatedHistory: true);
        var removal = CreateRemoval(scenario, scenario.Operation, removedIndex: 0);
        var cutoff = UserControlOperationEnvelopeUtil.DeserializeDeviceRemovalPayload(removal.OperationPayload);
        cutoff.Origins.Single().HighestAcceptedControlSequence = 1; // Retain the already-applied genesis-authored addition.
        UserControlOperationEnvelopeUtil.FinalizeDeviceRemovalPayload(cutoff);
        var json = System.Text.Json.Nodes.JsonNode.Parse(UserControlOperationEnvelopeUtil.SerializeDeviceRemovalPayload(cutoff))!;
        json["Origins"]![0]!["AdditionOperationHash"] = emptyHash ? "" : null;
        removal.OperationPayload = System.Text.Encoding.UTF8.GetBytes(json.ToJsonString());
        var decodedCutoff = UserControlOperationEnvelopeUtil.DeserializeDeviceRemovalPayload(removal.OperationPayload);
        Assert.IsNull(decodedCutoff.Origins.Single().AdditionOperationHash);
        CollectionAssert.AreEqual(cutoff.IntegrityHash, decodedCutoff.IntegrityHash);
        removal.OriginDeviceId = scenario.Peers[1].LocalDeviceId;
        removal.OriginInstanceId = scenario.Peers[1].OriginInstanceId;
        removal.OriginSequence = 1;
        UserControlOperationEnvelopeUtil.FillOriginAuthentication(removal, scenario.Peers[1]);
        var decoded = UserControlOperationEnvelopeUtil.Deserialize(UserControlOperationEnvelopeUtil.Serialize(removal));
        UserControlOperationEnvelopeUtil.VerifyWithSigningKey(decoded, scenario.Peers[1].SignPublicKey);
        await using var author = DeliveryNode.Wrap(scenario);
        Assert.AreEqual(UserControlOperationReceiptState.Applied, (await author.ApplyAsync(decoded)).State);
        Assert.AreEqual(RevocationDeliveryState.Completed, (await SnapshotAsync(author, decoded.OperationId)).State);
        var retained = (await author.Db.UserOriginRemovalCutoffs.ListForUserAsync(scenario.User.UId)).Single();
        Assert.AreEqual(1L, retained.HighestAcceptedControlSequence);
        CollectionAssert.AreEqual(removal.OperationHash, retained.RemovalOperationHash);
    }

    [TestMethod]
    public async Task OptionalHashNormalization_NeverAcceptsMalformedHashesOrSuppliesMissingAuthority()
    {
        await using var scenario = await Scenario.CreateAsync(peerCount: 2, authenticatedHistory: true);
        foreach (var name in new[] { "AdditionOperationHash", "RemovalOperationHash" })
        foreach (var length in new[] { 1, 31, 33 })
        {
            var json = System.Text.Json.Nodes.JsonNode.Parse(scenario.Operation.OperationPayload)!;
            json["KnownMembers"]![0]![name] = Convert.ToBase64String(new byte[length]);
            Assert.ThrowsExactly<InvalidDataException>(() => UserControlOperationEnvelopeUtil.DeserializeAccountDeletionPayload(
                System.Text.Encoding.UTF8.GetBytes(json.ToJsonString())));
        }
        var removal = CreateRemoval(scenario, scenario.Operation, removedIndex: 1);
        removal.RevocationMembers.Single(m => m.StartedMembershipEpoch == 2).AdditionOperationHash = [];
        UserControlOperationEnvelopeUtil.FillOriginAuthentication(removal, scenario.Peers[0]);
        await using var author = await DeliveryNode.CreateAsync(scenario, 0);
        Assert.AreEqual(UserControlOperationReceiptState.Applied, (await author.ApplyAsync(removal)).State);
        Assert.AreEqual(RevocationDeliveryState.Blocked, (await SnapshotAsync(author, removal.OperationId)).State,
            "Absent hashes cannot replace the signed addition proof needed to establish delivery membership.");
    }

    [TestMethod]
    public async Task Relay_ExportsRetainedVerifiedProofPrefixesWithoutChangingOriginalOperationIdentity()
    {
        await using var scenario = await Scenario.CreateAsync(peerCount: 2, authenticatedHistory: true);
        await using var relay = DeliveryNode.Wrap(scenario);
        scenario.Operation.RevocationHistoryProofs.Clear(); // Local Applied history can repair missing auxiliary bytes.
        await relay.ApplyAsync(scenario.Operation);
        Assert.AreNotEqual(RevocationDeliveryState.Blocked, (await SnapshotAsync(relay, scenario.Operation.OperationId)).State);
        var stored = (await relay.Db.UserControlOperations.GetByIdAsync(scenario.Operation.OperationId))!;
        var target = (await relay.Db.Devices.GetByIdAsync(scenario.Peers[0].LocalDeviceId))!;
        relay.Identity.IsSyncOn = false; // Passive control export must work after active delivery stops.
        var builder = new OutgoingDeltaBuilderService(null!, null!, null!, null!, relay.Identity, null!,
            new FakeUserMembershipAuthorizationService(), revocationEvidence: relay.Db.RevocationReceipts);
        var delta = await builder.BuildUserControlOperationRelayAsync(stored, target);
        // The focused fake identity returns plaintext; production uses the existing encrypted relay.
        var payload = System.Text.Json.JsonSerializer.Deserialize(delta.Payload,
            PasswordManagerLocal.Common.Backend.Utils.BackendJsonSerializerContext.Default.SyncDeltaPayload)!;
        var sent = payload.UserControlOperation!;
        Assert.AreEqual(1, sent.RevocationHistoryProofs.Count);
        CollectionAssert.AreEqual(scenario.Operation.OperationHash, sent.OperationHash);
        CollectionAssert.AreEqual(scenario.Operation.OriginSignature, sent.OriginSignature);
        UserControlOperationEnvelopeUtil.VerifyWithSigningKey(sent.RevocationHistoryProofs.Single(), scenario.Peers[0].SignPublicKey);
    }

    [TestMethod]
    public async Task MissingPeerEvidence_RemainsDurableUntilTheExactCommittedInventoryAcknowledgesIt()
    {
        await using var scenario = await Scenario.CreateAsync(peerCount: 2, authenticatedHistory: true);
        await using var author = await DeliveryNode.CreateAsync(scenario, 0);
        await using var peer = DeliveryNode.Wrap(scenario);
        await author.ApplyAsync(scenario.Operation); await peer.ApplyAsync(scenario.Operation);
        await ConvergeAsync(author, peer, scenario.Operation.OperationId);
        var inventory = new UserControlOperationUserInventory { UserId = scenario.User.UId.ToString("N") };
        inventory.Operations.Add(new UserControlOperationInventoryEntry { OperationId = scenario.Operation.OperationId.ToString("N"),
            OperationHash = Google.Protobuf.ByteString.CopyFrom(scenario.Operation.OperationHash) });
        inventory.Receipts.AddRange(await peer.Receipts.GetInventoryAsync(scenario.Operation.OperationId));
        var missing = inventory.Receipts.First(r => r.Kind == (int)RevocationReceiptKind.AppliedReceiptStored).Clone();
        inventory.Receipts.Remove(inventory.Receipts.Single(r => RevocationReceiptService.Key(r) == RevocationReceiptService.Key(missing)));
        await author.Receipts.ObservePeerInventoryAsync(peer.Identity.LocalDeviceId, [inventory]);
        await author.Receipts.ObservePeerInventoryAsync(peer.Identity.LocalDeviceId, [inventory]);
        using (var reopened = author.Db.OpenSession())
        {
            var persisted = new PasswordManagerLocal.Common.Backend.Repositories.RevocationReceiptRepository(reopened);
            var debt = (await persisted.ListDebtsAsync(scenario.Operation.OperationId)).Single();
            CollectionAssert.AreEqual(missing.ReceiptHash.ToByteArray(), debt.ReceiptHash);
        }
        Assert.AreNotEqual(RevocationDeliveryState.Completed, (await SnapshotAsync(author, scenario.Operation.OperationId)).State);
        var mismatched = missing.Clone();
        mismatched.ReceiptHash = Google.Protobuf.ByteString.CopyFrom(new byte[32]);
        inventory.Receipts.Add(mismatched);
        await author.Receipts.ObservePeerInventoryAsync(peer.Identity.LocalDeviceId, [inventory]);
        Assert.AreEqual(1, (await author.Db.RevocationReceipts.ListDebtsAsync(scenario.Operation.OperationId)).Count);
        inventory.Receipts.Remove(mismatched); inventory.Receipts.Add(missing);
        await author.Receipts.ObservePeerInventoryAsync(peer.Identity.LocalDeviceId, [inventory]);
        await author.Receipts.ObservePeerInventoryAsync(peer.Identity.LocalDeviceId, [inventory]);
        Assert.AreEqual(0, (await author.Db.RevocationReceipts.ListDebtsAsync(scenario.Operation.OperationId)).Count,
            "Deleting a remembered debt must compare its binary hash key by content.");
        Assert.AreEqual(RevocationDeliveryState.Completed, (await SnapshotAsync(author, scenario.Operation.OperationId)).State);
    }

    [TestMethod]
    public async Task Completion_RequiresSignedCustody_ThenSurvivesReopenWithoutErasingHistory()
    {
        await using var scenario = await Scenario.CreateAsync(peerCount: 2, authenticatedHistory: true);
        await using var author = await DeliveryNode.CreateAsync(scenario, 0);
        await using var recipient = DeliveryNode.Wrap(scenario);
        await author.ApplyAsync(scenario.Operation); await recipient.ApplyAsync(scenario.Operation);
        Assert.IsTrue(await author.Delivery.HasPendingDeliveryAsync());
        Assert.IsTrue(await recipient.Delivery.HasPendingForPeerAsync(scenario.Peers[0].LocalDeviceId));
        await TransferAsync(recipient, author, scenario.Operation.OperationId);
        var partial = await SnapshotAsync(author, scenario.Operation.OperationId);
        Assert.AreEqual(2, partial.AppliedRecipients);
        Assert.IsTrue(partial.HasUnexportedEvidence, "Knowing both application signatures alone must not strand the last receipt.");
        Assert.AreNotEqual(RevocationDeliveryState.Completed, partial.State);
        await TransferAsync(author, recipient, scenario.Operation.OperationId);
        await TransferAsync(recipient, author, scenario.Operation.OperationId);
        Assert.AreEqual(RevocationDeliveryState.Completed, (await SnapshotAsync(author, scenario.Operation.OperationId)).State);
        Assert.AreEqual(RevocationDeliveryState.Completed, (await SnapshotAsync(recipient, scenario.Operation.OperationId)).State);
        using var session = author.Db.OpenSession();
        var reopened = new RevocationDeliveryService(new PasswordManagerLocal.Common.Backend.Repositories.RevocationReceiptRepository(session),
            new PasswordManagerLocal.Common.Backend.Repositories.UserControlOperationRepository(session), new PasswordManagerLocal.Common.Backend.Repositories.UserControlStateRepository(session),
            new PasswordManagerLocal.Common.Backend.Repositories.DeletedUserBarrierRepository(session), new PasswordManagerLocal.Common.Backend.Repositories.UserMembershipAuthorizationRepository(session));
        Assert.IsFalse(await reopened.HasPendingDeliveryAsync());
        Assert.IsNotNull(await author.Db.UserControlOperations.GetByIdAsync(scenario.Operation.OperationId));
        Assert.IsNotNull(await author.Db.DeletedUserBarriers.GetAsync(scenario.User.UId));
    }

    [TestMethod]
    public async Task OfflineRecipient_AndThreeDeviceRelay_ConvergeWithoutAuthorRecipientContact()
    {
        await using var scenario = await Scenario.CreateAsync(peerCount: 3, authenticatedHistory: true);
        await using var author = await DeliveryNode.CreateAsync(scenario, 0);
        await using var relay = DeliveryNode.Wrap(scenario);
        await using var offline = await DeliveryNode.CreateAsync(scenario, 2);
        await author.ApplyAsync(scenario.Operation); await relay.ApplyAsync(scenario.Operation);
        await TransferAsync(author, relay, scenario.Operation.OperationId); await TransferAsync(relay, author, scenario.Operation.OperationId);
        var partial = await SnapshotAsync(author, scenario.Operation.OperationId);
        Assert.AreEqual(3, partial.RequiredRecipients); Assert.AreEqual(2, partial.AppliedRecipients);
        Assert.AreNotEqual(RevocationDeliveryState.Completed, partial.State);
        await offline.ApplyAsync(scenario.Operation);
        for (var round = 0; round < 3; round++)
        {
            await TransferAsync(relay, offline, scenario.Operation.OperationId);
            await TransferAsync(offline, relay, scenario.Operation.OperationId);
            await TransferAsync(relay, author, scenario.Operation.OperationId);
            await TransferAsync(author, relay, scenario.Operation.OperationId);
        }
        foreach (var node in new[] { author, relay, offline })
            Assert.AreEqual(RevocationDeliveryState.Completed, (await SnapshotAsync(node, scenario.Operation.OperationId)).State);
        Assert.AreEqual(3, (await author.Db.RevocationReceipts.ListAsync(scenario.Operation.OperationId))
            .Count(r => r.Kind == RevocationReceiptKind.Applied));
    }

    [TestMethod]
    public async Task RemovedTargetIsBestEffort_WithoutPretendingThatItAcknowledged()
    {
        await using var scenario = await Scenario.CreateAsync(peerCount: 3, authenticatedHistory: true);
        await using var author = await DeliveryNode.CreateAsync(scenario, 0);
        await using var peer = DeliveryNode.Wrap(scenario);
        var removal = CreateRemoval(scenario, scenario.Operation, removedIndex: 2);
        await author.ApplyAsync(removal); await peer.ApplyAsync(removal);
        await ConvergeAsync(author, peer, removal.OperationId);
        var completed = await SnapshotAsync(author, removal.OperationId);
        Assert.AreEqual(2, completed.RequiredRecipients);
        Assert.AreEqual(RevocationDeliveryState.Completed, completed.State);
        Assert.IsFalse((await author.Db.RevocationReceipts.ListAsync(removal.OperationId))
            .Any(r => r.RecipientDeviceId == scenario.Peers[2].LocalDeviceId));
        Assert.IsFalse((await author.Db.UserMembershipAuthorizations.GetByIdAsync(scenario.Members[2].AuthorizationId))!.IsActive);
        Assert.IsTrue(await author.Db.UserControlOperations.HasAppliedRevocationAsync());
    }

    [TestMethod]
    public async Task CompletedLaterRemoval_RetiresOldOfflineObligationWithoutAnInventedReceipt()
    {
        await using var scenario = await Scenario.CreateAsync(peerCount: 4, authenticatedHistory: true);
        await using var author = await DeliveryNode.CreateAsync(scenario, 0);
        await using var peer = DeliveryNode.Wrap(scenario);
        var first = CreateRemoval(scenario, scenario.Operation, removedIndex: 3);
        await author.ApplyAsync(first); await peer.ApplyAsync(first);
        await ConvergeAsync(author, peer, first.OperationId);
        Assert.AreNotEqual(RevocationDeliveryState.Completed, (await SnapshotAsync(author, first.OperationId)).State);
        var user = (await author.Db.Users.GetByIdAsync(scenario.User.UId))!;
        var later = await author.Writer.CreateAppliedDeviceRemovalAsync(user,
            RemovalPayload(user, scenario.Members[2]));
        await peer.ApplyAsync(later);
        await ConvergeAsync(author, peer, later.OperationId);
        var complete = await SnapshotAsync(author, first.OperationId);
        Assert.AreEqual(RevocationDeliveryState.Completed, complete.State);
        Assert.AreEqual(1, complete.RetiredRecipients);
        Assert.AreEqual(2, complete.AppliedRecipients);
        Assert.IsFalse((await author.Db.RevocationReceipts.ListAsync(first.OperationId))
            .Any(r => r.Kind == RevocationReceiptKind.Applied && r.RecipientDeviceId == scenario.Peers[2].LocalDeviceId));
    }

    [TestMethod]
    public async Task DeletedAccountWithMissedRemoval_VerifiesHistoricalProofsWithoutRestoringLiveMembership()
    {
        await using var scenario = await Scenario.CreateAsync(peerCount: 4, authenticatedHistory: true);
        await using var author = await DeliveryNode.CreateAsync(scenario, 0);
        await using var peer = DeliveryNode.Wrap(scenario);
        await using var offline = await DeliveryNode.CreateAsync(scenario, 2);
        var first = CreateRemoval(scenario, scenario.Operation, removedIndex: 3);
        await author.ApplyAsync(first); await peer.ApplyAsync(first);
        var user = (await author.Db.Users.GetByIdAsync(scenario.User.UId))!;
        var deletion = await author.CreateDeletionAsync(user);
        await peer.ApplyAsync(deletion);
        // The offline installation has never applied the earlier removal, but deletion carries
        // independently signed history proofs and the original author is historically known.
        await offline.ApplyAsync(deletion);
        Assert.IsNull(await offline.Db.Users.GetByIdAsync(user.UId));
        Assert.IsNotNull(await offline.Db.DeletedUserBarriers.GetAsync(user.UId));
        Assert.IsTrue((await offline.Db.UserMembershipAuthorizations.GetByIdAsync(scenario.Members[3].AuthorizationId))!.IsActive,
            "Receipt-only proof reconstruction must not mutate the live membership projection.");
        for (var round = 0; round < 3; round++)
        {
            await TransferAsync(author, peer, deletion.OperationId); await TransferAsync(peer, author, deletion.OperationId);
            await TransferAsync(peer, offline, deletion.OperationId); await TransferAsync(offline, peer, deletion.OperationId);
            await TransferAsync(peer, author, deletion.OperationId);
        }
        Assert.AreEqual(RevocationDeliveryState.Completed, (await SnapshotAsync(author, deletion.OperationId)).State);
        Assert.AreEqual(RevocationDeliveryState.Completed, (await SnapshotAsync(offline, deletion.OperationId)).State);
        Assert.AreEqual(RevocationDeliveryState.Completed, (await SnapshotAsync(author, first.OperationId)).State,
            "A fully delivered terminal barrier safely discharges the older security-delivery obligation.");
    }

    [TestMethod]
    public async Task MissingMembershipProofs_BlockCompletion_AndAuthenticatedDuplicateRepairsOnlyEvidence()
    {
        await using var scenario = await Scenario.CreateAsync(peerCount: 2, authenticatedHistory: true);
        await using var node = DeliveryNode.Wrap(scenario);
        // Remove the locally retained addition too, to model a terminal-deletion receiver with incomplete public history.
        node.Db.UserControlOperations.DeleteRange(await node.Db.UserControlOperations.ListForUserAsync(scenario.User.UId));
        await node.Db.UnitOfWork.SaveChangesAsync();
        var completeProofs = scenario.Operation.RevocationHistoryProofs.ToList();
        scenario.Operation.RevocationHistoryProofs.Clear(); // Auxiliary proof loss cannot change immutable operation identity.
        await node.ApplyAsync(scenario.Operation);
        Assert.AreEqual(RevocationDeliveryState.Blocked, (await SnapshotAsync(node, scenario.Operation.OperationId)).State);
        scenario.Operation.RevocationHistoryProofs.AddRange(completeProofs);
        await node.ApplyAsync(scenario.Operation);
        Assert.AreNotEqual(RevocationDeliveryState.Blocked, (await SnapshotAsync(node, scenario.Operation.OperationId)).State);
        Assert.IsNull(await node.Db.Users.GetByIdAsync(scenario.User.UId));
    }

    [TestMethod]
    public async Task SignedRecipientManifestAndCustodyFields_RejectTamperingAndSelfCustody()
    {
        await using var scenario = await Scenario.CreateAsync(peerCount: 2, authenticatedHistory: true);
        var bytes = UserControlOperationEnvelopeUtil.Serialize(scenario.Operation);
        var changed = UserControlOperationEnvelopeUtil.Deserialize(bytes);
        changed.RevocationMembers.RemoveAt(1);
        Assert.ThrowsExactly<InvalidDataException>(() => UserControlOperationEnvelopeUtil.ValidateStructureAndHash(changed));
        await using var author = await DeliveryNode.CreateAsync(scenario, 0);
        await author.ApplyAsync(scenario.Operation);
        var applied = (await author.Db.RevocationReceipts.ListAsync(scenario.Operation.OperationId)).Single(r => r.Kind == RevocationReceiptKind.Applied);
        var self = RevocationDeliveryReceiptUtil.Create(scenario.Operation, scenario.Members[0],
            RevocationReceiptKind.AppliedReceiptStored, scenario.Peers[0], applied.ReceiptHash);
        await Assert.ThrowsExactlyAsync<InvalidDataException>(() => author.Receipts.StoreAsync(
            [RevocationDeliveryReceiptUtil.Serialize(self)], new HashSet<Guid> { scenario.Operation.OperationId }));
        self.AttestedReceiptHash[0] ^= 1;
        Assert.ThrowsExactly<InvalidDataException>(() => RevocationDeliveryReceiptUtil.Verify(self, scenario.Operation, scenario.Members[0]));
    }

    [TestMethod]
    public async Task AuthorityConflictAndConfirmedHistoryReset_RemainBlockedWithoutDiscardingReceipts()
    {
        await using var scenario = await Scenario.CreateAsync(peerCount: 2, authenticatedHistory: true);
        await using var node = DeliveryNode.Wrap(scenario);
        await node.ApplyAsync(scenario.Operation);
        var state = (await node.Db.UserControlStates.GetAsync(scenario.User.UId))!;
        state.HasConflict = true; node.Db.UserControlStates.Update(state); await node.Db.UnitOfWork.SaveChangesAsync();
        Assert.AreEqual(RevocationDeliveryState.Blocked, (await SnapshotAsync(node, scenario.Operation.OperationId)).State);
        await using (var transaction = await node.Db.UnitOfWork.BeginTransactionAsync())
        {
            await node.Delivery.StageHistoryResetAsync(scenario.User.UId);
            await node.Db.UnitOfWork.SaveChangesAsync(); await transaction.CommitAsync();
        }
        state = (await node.Db.UserControlStates.GetAsync(scenario.User.UId))!;
        state.HasConflict = false; node.Db.UserControlStates.Update(state);
        node.Db.UserControlOperations.DeleteRange(await node.Db.UserControlOperations.ListForUserAsync(scenario.User.UId));
        node.Db.UserMembershipAuthorizations.DeleteRange(await node.Db.UserMembershipAuthorizations.ListForUserAsync(scenario.User.UId));
        await node.Db.UnitOfWork.SaveChangesAsync();
        Assert.AreEqual(RevocationDeliveryState.Blocked, (await SnapshotAsync(node, scenario.Operation.OperationId)).State);
        Assert.AreEqual(1, (await node.Db.RevocationReceipts.ListAsync(scenario.Operation.OperationId)).Count(r => r.Kind == RevocationReceiptKind.Applied));
        Assert.IsNotNull(await node.Db.DeletedUserBarriers.GetAsync(scenario.User.UId));
    }

    [TestMethod]
    public async Task CompletedHistory_DoesNotDriveRuntime_AndFinalExchangeLeaseDrainsBeforeShutdown()
    {
        await using var scenario = await Scenario.CreateAsync(peerCount: 2, authenticatedHistory: true);
        await using var author = await DeliveryNode.CreateAsync(scenario, 0);
        await using var peer = DeliveryNode.Wrap(scenario);
        await author.ApplyAsync(scenario.Operation); await peer.ApplyAsync(scenario.Operation);
        var profiles = new FakeLocalUserDeviceRepository(); var activity = new RevocationDeliveryActivity();
        var services = new ServiceCollection();
        services.AddSingleton<ILocalUserDeviceRepository>(profiles);
        services.AddSingleton<IRevocationDeliveryService>(author.Delivery);
        services.AddSingleton<IRevocationDeliveryActivity>(activity);
        using var provider = services.BuildServiceProvider();
        author.Identity.IsSyncOn = false;
        var runtime = new SyncRuntimeService(provider.GetRequiredService<IServiceScopeFactory>(), author.Identity,
            new EnrollmentRuntimeState(), new FakeSyncDeviceIdentityService(), new DiscoveredDeviceEndpointRegistry(),
            new FakeDeviceSyncTaskService(), []);
        await runtime.RefreshSyncEnabledAsync(); Assert.IsTrue(author.Identity.IsSyncOn);
        await ConvergeAsync(author, peer, scenario.Operation.OperationId);
        using (activity.Enter())
        {
            await runtime.RefreshSyncEnabledAsync(); Assert.IsTrue(author.Identity.IsSyncOn);
        }
        await runtime.RefreshSyncEnabledAsync(); Assert.IsFalse(author.Identity.IsSyncOn);
        Assert.AreEqual(SyncRuntimeState.Disabled, runtime.Snapshot.State);
        Assert.IsTrue(await author.Db.UserControlOperations.HasAppliedRevocationAsync());
        await profiles.AddAsync(new LocalUserDevice { UserId = Guid.NewGuid(), LocalDeviceIdentityId = author.Identity.LocalDeviceId, IsSyncOn = true });
        await runtime.RefreshSyncEnabledAsync(); Assert.IsTrue(author.Identity.IsSyncOn);
        await runtime.StopAsync();
    }

    [TestMethod]
    public async Task ANewRevocationAfterCompletion_ReactivatesDeliveryWithoutReactivatingOldHistory()
    {
        await using var scenario = await Scenario.CreateAsync(peerCount: 2, authenticatedHistory: true);
        await using var author = await DeliveryNode.CreateAsync(scenario, 0);
        await using var peer = DeliveryNode.Wrap(scenario);
        await author.ApplyAsync(scenario.Operation); await peer.ApplyAsync(scenario.Operation);
        await ConvergeAsync(author, peer, scenario.Operation.OperationId);
        var services = new ServiceCollection();
        services.AddSingleton<ILocalUserDeviceRepository>(new FakeLocalUserDeviceRepository());
        services.AddSingleton<IRevocationDeliveryService>(author.Delivery);
        using var provider = services.BuildServiceProvider();
        var runtime = new SyncRuntimeService(provider.GetRequiredService<IServiceScopeFactory>(), author.Identity,
            new EnrollmentRuntimeState(), new FakeSyncDeviceIdentityService(), new DiscoveredDeviceEndpointRegistry(), new FakeDeviceSyncTaskService(), []);
        await runtime.RefreshSyncEnabledAsync(); Assert.IsFalse(author.Identity.IsSyncOn);
        await using var next = await Scenario.CreateAsync(peerCount: 2, authenticatedHistory: true);
        await author.Db.Users.AddAsync(next.User);
        foreach (var member in next.Members) await author.Db.UserMembershipAuthorizations.AddAsync(member);
        foreach (var proof in next.Operation.RevocationHistoryProofs)
            await author.Db.UserControlOperations.AddAsync(UserControlOperationMapping.ToStoredOperation(proof,
                UserControlOperationEnvelopeUtil.Serialize(proof), UserControlOperationStatus.Applied, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow));
        await author.Db.UnitOfWork.SaveChangesAsync();
        await author.ApplyAsync(next.Operation);
        await runtime.RefreshSyncEnabledAsync(); Assert.IsTrue(author.Identity.IsSyncOn);
        CollectionAssert.AreEqual(new[] { next.User.UId }, (await author.Delivery.ListPendingUserIdsAsync()).ToArray());
        Assert.AreEqual(RevocationDeliveryState.Completed, (await SnapshotAsync(author, scenario.Operation.OperationId)).State);
        await runtime.StopAsync();
    }

    [TestMethod]
    public async Task FiveDevicePartitionAndDuplicateCustody_ConvergeWithBoundedReceiptBatches()
    {
        await using var scenario = await Scenario.CreateAsync(peerCount: 5, authenticatedHistory: true);
        var nodes = new List<DeliveryNode>();
        try
        {
            for (var index = 0; index < 5; index++) nodes.Add(index == 1 ? DeliveryNode.Wrap(scenario) : await DeliveryNode.CreateAsync(scenario, index));
            foreach (var node in nodes) await node.ApplyAsync(scenario.Operation);
            // A line topology exercises multi-hop receipt and custody relay without direct end-to-end contact.
            for (var pass = 0; pass < 5; pass++)
                for (var index = 0; index < 4; index++)
                {
                    await TransferAsync(nodes[index], nodes[index + 1], scenario.Operation.OperationId);
                    await TransferAsync(nodes[index + 1], nodes[index], scenario.Operation.OperationId);
                }
            foreach (var node in nodes)
                Assert.AreEqual(RevocationDeliveryState.Completed, (await SnapshotAsync(node, scenario.Operation.OperationId)).State);
            var count = (await nodes[0].Db.RevocationReceipts.ListAsync(scenario.Operation.OperationId)).Count;
            await TransferAsync(nodes[1], nodes[0], scenario.Operation.OperationId);
            Assert.AreEqual(count, (await nodes[0].Db.RevocationReceipts.ListAsync(scenario.Operation.OperationId)).Count);
        }
        finally { foreach (var node in nodes) await node.DisposeAsync(); }
    }

    private static async Task<RevocationDeliverySnapshot> SnapshotAsync(DeliveryNode node, Guid id) =>
        (await node.Delivery.GetSnapshotsAsync()).Single(s => s.OperationId == id);

    private static async Task ConvergeAsync(DeliveryNode a, DeliveryNode b, Guid operation)
    {
        for (var round = 0; round < 3; round++) { await TransferAsync(a, b, operation); await TransferAsync(b, a, operation); }
    }

    private static async Task TransferAsync(DeliveryNode from, DeliveryNode to, Guid operation)
    {
        var payloads = (await from.Db.RevocationReceipts.ListAsync(operation)).OrderBy(r => r.Kind == RevocationReceiptKind.AppliedReceiptStored)
            .Select(r => r.Payload).ToArray();
        foreach (var batch in payloads.Chunk(PasswordManagerLocal.Common.Backend.Constants.SyncConstants.MaxRevocationReceiptsPerCall))
            await to.Receipts.StoreAsync(batch, new HashSet<Guid> { operation });
    }

    private static UserControlOperationEnvelope CreateRemoval(Scenario scenario, UserControlOperationEnvelope template, int removedIndex)
    {
        var operation = UserControlOperationEnvelopeUtil.CopyCoreProof(template);
        operation.RevocationHistoryProofs.AddRange(template.RevocationHistoryProofs);
        operation.OperationType = UserControlOperationType.DeviceRemoval;
        operation.ResultingMembershipEpoch = template.PreviousMembershipEpoch + 1;
        operation.OperationPayload = UserControlOperationEnvelopeUtil.SerializeDeviceRemovalPayload(RemovalPayload(scenario.User, scenario.Members[removedIndex]));
        UserControlOperationEnvelopeUtil.FillOriginAuthentication(operation, scenario.Peers[0]);
        return operation;
    }

    private static DeviceRemovalPayload RemovalPayload(User user, UserMembershipAuthorization member)
    {
        var result = new DeviceRemovalPayload
        {
            UserId = user.UId, RemovedDeviceId = member.DeviceId, KeyEpoch = user.KeyEpoch,
            PreviousMembershipEpoch = user.MembershipEpoch, ResultingMembershipEpoch = user.MembershipEpoch + 1,
            Origins = [new DeviceRemovalOriginCutoffPayload { AuthorizationId = member.AuthorizationId, OriginInstanceId = member.OriginInstanceId,
                UserKeyEpoch = user.KeyEpoch, HighestAcceptedControlSequence = 0, HighestAcceptedSnapshotRevision = 0,
                SignPublicKeyHash = member.SignPublicKeyHash, AdditionOperationId = member.AdditionOperationId, AdditionOperationHash = member.AdditionOperationHash }]
        };
        UserControlOperationEnvelopeUtil.FinalizeDeviceRemovalPayload(result);
        return result;
    }

    private sealed class DeliveryNode : IAsyncDisposable
    {
        private readonly bool _owns;
        private readonly UserLifecycleCoordinator _lifecycle = new();
        public SqliteRepositoryTestDatabase Db { get; }
        public FakeDeviceIdentityService Identity { get; }
        public RevocationReceiptService Receipts { get; }
        public RevocationDeliveryService Delivery { get; }
        public UserControlOperationInboxService Inbox { get; }
        public UserControlOperationWriterService Writer { get; }
        private DeliveryNode(SqliteRepositoryTestDatabase database, FakeDeviceIdentityService identity, bool owns)
        {
            Db = database; Identity = identity; _owns = owns;
            var membership = new UserMembershipAuthorizationService(Db.UserMembershipAuthorizations, Db.UserOriginRemovalCutoffs, identity);
            Receipts = new(Db.RevocationReceipts, Db.UserMembershipAuthorizations, Db.UserControlOperations, identity, _lifecycle, Db.UnitOfWork, Db.Devices);
            Delivery = new(Db.RevocationReceipts, Db.UserControlOperations, Db.UserControlStates, Db.DeletedUserBarriers, Db.UserMembershipAuthorizations);
            Inbox = new(Db.UserControlOperations, Db.UserControlStates, Db.Users, Db.Devices, Db.UserDevices, Db.UserSyncSnapshots,
                _lifecycle, new FakeInteractiveSessionStateService(new FakeAuthService()), Db.UnitOfWork, membership,
                Db.UserMembershipAuthorizations, Db.LocalUserDevices, identity, new FakeSyncRuntimeService(), Db.DeletedUserBarriers,
                new UserAccountDeletionCleanupService(new PasswordManagerLocal.Common.Backend.Repositories.UserAccountDeletionStore(Db.UnitOfWork, Db.Users, Db.DeviceEnrollmentCommits), Db.Users),
                revocationReceipts: Receipts);
            Writer = new(Db.UserControlOperations, Db.UserControlStates, Db.Users, Db.Devices, Db.UserDevices,
                Db.UserMembershipAuthorizations, membership, identity, _lifecycle, Db.UnitOfWork, revocationReceipts: Receipts);
        }
        public static DeliveryNode Wrap(Scenario scenario) => new(scenario.Db, scenario.Peers[1], false);
        public static async Task<DeliveryNode> CreateAsync(Scenario scenario, int index)
        {
            var db = await SqliteRepositoryTestDatabase.CreateAsync();
            await db.Users.AddAsync(scenario.User);
            foreach (var member in scenario.Members) await db.UserMembershipAuthorizations.AddAsync(member);
            foreach (var peer in scenario.Peers)
            {
                var device = new Device { Id = peer.LocalDeviceId, SignPublicKey = peer.SignPublicKey, PublicKey = peer.AgreementPublicKey,
                    TlsCertFingerprint = peer.FingerprintHex, DeviceType = peer.DeviceType, IsTrusted = true };
                device.GenerateIntegrityHash(); await db.Devices.AddAsync(device);
            }
            foreach (var operation in scenario.Operation.RevocationHistoryProofs)
                await db.UserControlOperations.AddAsync(UserControlOperationMapping.ToStoredOperation(operation,
                    UserControlOperationEnvelopeUtil.Serialize(operation), UserControlOperationStatus.Applied, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow));
            await db.UserControlStates.AddAsync(new UserControlState { UserId = scenario.User.UId, LocalOriginInstanceId = scenario.Peers[index].OriginInstanceId,
                NextOriginSequence = index == 0 ? scenario.Peers.Length + 1 : 1, AppliedKeyEpoch = 1, AppliedMembershipEpoch = scenario.User.MembershipEpoch });
            await db.UnitOfWork.SaveChangesAsync();
            return new(db, scenario.Peers[index], true);
        }
        public Task<UserControlOperationReceiptResult> ApplyAsync(UserControlOperationEnvelope operation) => Inbox.StoreAndApplyAsync(operation, Guid.NewGuid());
        public async Task<UserControlOperationEnvelope> CreateDeletionAsync(User user)
        {
            await using var transaction = await Db.UnitOfWork.BeginTransactionAsync();
            var operation = await Writer.CreateAppliedAccountDeletionUnderLifecycleAsync(user);
            var payload = UserControlOperationEnvelopeUtil.DeserializeAccountDeletionPayload(operation.OperationPayload);
            await Db.DeletedUserBarriers.AddAsync(DeletedUserBarrierUtil.Create(operation, payload, DateTimeOffset.UtcNow));
            await new UserAccountDeletionCleanupService(new PasswordManagerLocal.Common.Backend.Repositories.UserAccountDeletionStore(Db.UnitOfWork, Db.Users, Db.DeviceEnrollmentCommits), Db.Users)
                .DeleteCanonicalAndPendingStateAsync(user.UId);
            await Receipts.StageAppliedAsync(operation); await Db.UnitOfWork.SaveChangesAsync(); await transaction.CommitAsync();
            return operation;
        }
        public async ValueTask DisposeAsync() { if (_owns) await Db.DisposeAsync(); }
    }
}
