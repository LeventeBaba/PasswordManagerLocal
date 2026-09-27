using Microsoft.VisualStudio.TestTools.UnitTesting;
using PasswordManagerLocal.Common.Backend.Models;
using PasswordManagerLocal.Common.Backend.Models.Encrypted;
using PasswordManagerLocal.Common.Backend.Services;
using PasswordManagerLocal.Common.Backend.Security;
using PasswordManagerLocal.Common.Backend.Sync;

using MSTestAssert = Microsoft.VisualStudio.TestTools.UnitTesting.Assert;

using PasswordManagerLocal.Common.Tests.TestInfrastructure.Services.Fixtures;
using PasswordManagerLocal.Common.Backend.Sync.Tombstones;
namespace PasswordManagerLocal.Common.Tests.Backend.Services;

[TestClass]
public sealed class UserTombstoneGarbageCollectorTests
{
    [TestMethod]
    [TestCategory("Backend")]
    public void Evaluate_ActiveDeviceWithoutMergedReceipt_IsUnstable()
    {
        var fixture = UserTombstoneGarbageCollectorFixture.Create();
        var receipts = fixture.ReceiptsFor(fixture.Origin);

        var result = fixture.Evaluate(receipts: receipts);

        MSTestAssert.AreEqual(TombstoneGarbageCollectionReason.MissingMergedReceipt, result.Reason);
        MSTestAssert.AreEqual(fixture.Peer.DeviceId, result.BlockingDeviceId);
    }

    [TestMethod]
    [TestCategory("Backend")]
    public void Evaluate_StoredOnlyAnchorKnowledge_IsInsufficient()
    {
        var fixture = UserTombstoneGarbageCollectorFixture.Create();
        fixture.Knowledge[fixture.AnchorKey] = Knowledge(fixture.Reference, stored: 7, merged: 6);

        var result = fixture.Evaluate(receipts: fixture.ReceiptsFor(fixture.Origin, fixture.Peer));

        MSTestAssert.AreEqual(TombstoneGarbageCollectionReason.StoredOnlyKnowledge, result.Reason);
    }

    [TestMethod]
    [TestCategory("Backend")]
    public void Evaluate_AllRelevantDevicesCoverDeletion_IsStable()
    {
        var fixture = UserTombstoneGarbageCollectorFixture.Create();

        var result = fixture.Evaluate(receipts: fixture.ReceiptsFor(fixture.Origin, fixture.Peer));

        MSTestAssert.AreEqual(TombstoneGarbageCollectionReason.Stable, result.Reason);
    }

    [TestMethod]
    [TestCategory("Backend")]
    public void Evaluate_DeviceRemovedBeforeDeletion_DoesNotBlock()
    {
        var fixture = UserTombstoneGarbageCollectorFixture.Create();
        var removedBefore = Authorization(Guid.NewGuid(), Guid.NewGuid(), started: 1, ended: 2, active: false);
        removedBefore.UserId = fixture.UserId;
        fixture.Authorizations.Add(removedBefore);

        var result = fixture.Evaluate(receipts: fixture.ReceiptsFor(fixture.Origin, fixture.Peer));

        MSTestAssert.AreEqual(TombstoneGarbageCollectionReason.Stable, result.Reason);
    }

    [TestMethod]
    [TestCategory("Backend")]
    public void Evaluate_DeviceRemovedAfterDeletion_WithMergedCutoff_DoesNotBlock()
    {
        var fixture = UserTombstoneGarbageCollectorFixture.Create(includePeer: false);
        var removed = Authorization(Guid.NewGuid(), Guid.NewGuid(), started: 1, ended: 3, active: false);
        fixture.Authorizations.Add(removed);
        fixture.AddRemovalEvidence(removed, acceptedRevision: 5, mergedRevision: 5);

        var result = fixture.Evaluate(receipts: fixture.ReceiptsFor(fixture.Origin));

        MSTestAssert.AreEqual(TombstoneGarbageCollectionReason.Stable, result.Reason);
    }

    [TestMethod]
    [TestCategory("Backend")]
    public void Evaluate_DeviceRemovedAfterDeletion_WithZeroAcceptedRevision_DoesNotRequireKnowledgeRow()
    {
        var fixture = UserTombstoneGarbageCollectorFixture.Create(includePeer: false);
        var removed = Authorization(Guid.NewGuid(), Guid.NewGuid(), started: 1, ended: 3, active: false);
        fixture.Authorizations.Add(removed);
        fixture.AddRemovalEvidence(removed, acceptedRevision: 0, mergedRevision: null);

        var result = fixture.Evaluate(receipts: fixture.ReceiptsFor(fixture.Origin));

        MSTestAssert.AreEqual(TombstoneGarbageCollectionReason.Stable, result.Reason);
    }

    [TestMethod]
    [TestCategory("Backend")]
    public void Evaluate_DeviceRemovedAfterDeletion_WithUnmergedAcceptedRevision_Blocks()
    {
        var fixture = UserTombstoneGarbageCollectorFixture.Create(includePeer: false);
        var removed = Authorization(Guid.NewGuid(), Guid.NewGuid(), started: 1, ended: 3, active: false);
        fixture.Authorizations.Add(removed);
        fixture.AddRemovalEvidence(removed, acceptedRevision: 5, mergedRevision: 4);

        var result = fixture.Evaluate(receipts: fixture.ReceiptsFor(fixture.Origin));

        MSTestAssert.AreEqual(TombstoneGarbageCollectionReason.RemovalCutoffNotMerged, result.Reason);
        MSTestAssert.AreEqual(removed.DeviceId, result.BlockingDeviceId);
    }

    [TestMethod]
    [TestCategory("Backend")]
    public void Evaluate_DeviceEnrolledAfterDeletion_DoesNotBecomeHistoricalBlocker()
    {
        var fixture = UserTombstoneGarbageCollectorFixture.Create();
        var addedAfter = Authorization(Guid.NewGuid(), Guid.NewGuid(), started: 3, ended: null, active: true);
        addedAfter.UserId = fixture.UserId;
        fixture.Authorizations.Add(addedAfter);

        var result = fixture.Evaluate(receipts: fixture.ReceiptsFor(fixture.Origin, fixture.Peer));

        MSTestAssert.AreEqual(TombstoneGarbageCollectionReason.Stable, result.Reason);
    }

    [TestMethod]
    [TestCategory("Backend")]
    public void Evaluate_CurrentEpochReplacementReceipt_CanCoverPriorEpochDeletion()
    {
        var fixture = UserTombstoneGarbageCollectorFixture.Create();
        var receipts = fixture.ReceiptsForKeyEpoch(2, fixture.Origin, fixture.Peer);

        var result = fixture.Evaluate(receipts: receipts);

        MSTestAssert.AreEqual(TombstoneGarbageCollectionReason.Stable, result.Reason);
    }

    [TestMethod]
    [TestCategory("Backend")]
    public void Evaluate_UnknownDeletionOriginMembership_Blocks()
    {
        var fixture = UserTombstoneGarbageCollectorFixture.Create(includeOrigin: false);

        var result = fixture.Evaluate(receipts: fixture.ReceiptsFor(fixture.Peer));

        MSTestAssert.AreEqual(TombstoneGarbageCollectionReason.UnknownHistoricalMembership, result.Reason);
    }

    [TestMethod]
    [TestCategory("Backend")]
    public void Evaluate_DifferentEnumerationOrders_ProduceSameDecision()
    {
        var fixture = UserTombstoneGarbageCollectorFixture.Create();
        var first = fixture.Evaluate(
            authorizations: fixture.Authorizations.ToArray(),
            receipts: fixture.ReceiptsFor(fixture.Origin, fixture.Peer));
        var reversedReceipts = fixture.ReceiptsFor(fixture.Peer, fixture.Origin)
            .ToDictionary(item => item.Key, item => item.Value.AsEnumerable().Reverse().ToList());
        var second = fixture.Evaluate(
            authorizations: fixture.Authorizations.AsEnumerable().Reverse().ToArray(),
            receipts: reversedReceipts);

        MSTestAssert.AreEqual(first, second);
        MSTestAssert.AreEqual(TombstoneGarbageCollectionReason.Stable, second.Reason);
    }

    [TestMethod]
    [TestCategory("Backend")]
    public void SortRemainingTombstones_UsesDeterministicItemIdOrder()
    {
        var low = Guid.Parse("00000000-0000-0000-0000-000000000001");
        var high = Guid.Parse("FFFFFFFF-FFFF-FFFF-FFFF-FFFFFFFFFFFF");
        using var bundle = new UserDataBundle();
        bundle.UserPasswordsData.DeletedPasswords.AddRange(
        [
            new DeletedPasswordData { Id = high },
            new DeletedPasswordData { Id = low }
        ]);
        bundle.UserPasswordsData.DeletedTags.AddRange(
        [
            new DeletedPasswordTagData { Id = high },
            new DeletedPasswordTagData { Id = low }
        ]);
        bundle.UserPasswordsData.DeletedCustomColors.AddRange(
        [
            new DeletedCustomUserColorData { Id = high },
            new DeletedCustomUserColorData { Id = low }
        ]);
        bundle.UserDevicesData.DeletedDevices.AddRange(
        [
            new DeletedUserDeviceData { Id = high },
            new DeletedUserDeviceData { Id = low }
        ]);

        TombstoneGarbageCollectionRules.SortRemainingTombstones(bundle);

        MSTestAssert.AreEqual(low, bundle.UserPasswordsData.DeletedPasswords[0].Id);
        MSTestAssert.AreEqual(low, bundle.UserPasswordsData.DeletedTags[0].Id);
        MSTestAssert.AreEqual(low, bundle.UserPasswordsData.DeletedCustomColors[0].Id);
        MSTestAssert.AreEqual(low, bundle.UserDevicesData.DeletedDevices[0].Id);
    }

    [TestMethod]
    [TestCategory("Backend")]
    public void Covers_StoredSnapshotWithoutCoverage_DoesNotClaimDeletion()
    {
        var fixture = UserTombstoneGarbageCollectorFixture.Create();
        var envelope = Receipt(fixture.Peer, keyEpoch: 1, coverage: null);

        MSTestAssert.IsFalse(TombstoneGarbageCollectionRules.Covers(envelope, fixture.Reference));
    }

    [TestMethod]
    [TestCategory("Backend")]
    public void ValidateEvidenceStructure_MissingMembershipTransition_FailsClosed()
    {
        var userId = Guid.NewGuid();
        var genesis = StructuralAuthorization(userId, started: 1, ended: null, active: true, genesis: true);
        var addedAtThree = StructuralAuthorization(userId, started: 3, ended: null, active: true, genesis: false);

        MSTestAssert.ThrowsExactly<InvalidDataException>(() =>
            TombstoneGarbageCollectionRules.ValidateEvidenceStructure(
                userId,
                currentKeyEpoch: 1,
                currentMembershipEpoch: 3,
                [genesis, addedAtThree],
                [],
                []));
    }

    [TestMethod]
    [TestCategory("Backend")]
    public void ValidateEvidenceStructure_MissingRemovalCutoffEpoch_FailsClosed()
    {
        var userId = Guid.NewGuid();
        var genesis = StructuralAuthorization(userId, started: 1, ended: null, active: true, genesis: true);
        var removed = StructuralAuthorization(userId, started: 2, ended: 3, active: false, genesis: false, maximumKeyEpoch: 2);
        var removalId = removed.RemovalOperationId!.Value;
        var removalHash = removed.RemovalOperationHash!.ToArray();
        var onlyFirstEpoch = new UserOriginRemovalCutoff
        {
            UserId = userId,
            DeviceId = removed.DeviceId,
            OriginInstanceId = removed.OriginInstanceId,
            UserKeyEpoch = 1,
            HighestAcceptedSnapshotRevision = 0,
            HighestAcceptedControlSequence = 0,
            ResultingMembershipEpoch = 3,
            AuthorizationId = removed.AuthorizationId,
            RemovalOperationId = removalId,
            RemovalOperationHash = removalHash
        };

        MSTestAssert.ThrowsExactly<InvalidDataException>(() =>
            TombstoneGarbageCollectionRules.ValidateEvidenceStructure(
                userId,
                currentKeyEpoch: 2,
                currentMembershipEpoch: 3,
                [genesis, removed],
                [onlyFirstEpoch],
                []));
    }

    private static UserMembershipAuthorization StructuralAuthorization(
        Guid userId,
        long started,
        long? ended,
        bool active,
        bool genesis,
        long maximumKeyEpoch = 1)
    {
        var signKey = Enumerable.Range(1, 32).Select(value => (byte)value).ToArray();
        var operationHash = Enumerable.Range(33, 32).Select(value => (byte)value).ToArray();
        return new UserMembershipAuthorization
        {
            AuthorizationId = Guid.NewGuid(),
            UserId = userId,
            DeviceId = Guid.NewGuid(),
            OriginInstanceId = Guid.NewGuid(),
            SignPublicKey = signKey,
            SignPublicKeyHash = Hashing.SHA256Hash(signKey),
            AgreementPublicKeyHash = Hashing.SHA256Hash([0x41]),
            TlsCertFingerprint = new string('A', 64),
            DeviceType = DeviceType.WindowsPc,
            StartedMembershipEpoch = started,
            EndedMembershipEpoch = ended,
            MinimumKeyEpoch = 1,
            MaximumKeyEpoch = active ? null : maximumKeyEpoch,
            IsActive = active,
            IsGenesis = genesis,
            AdditionOperationId = genesis ? null : Guid.NewGuid(),
            AdditionOperationHash = genesis ? null : Hashing.SHA256Hash([0x42]),
            RemovalOperationId = active ? null : Guid.NewGuid(),
            RemovalOperationHash = active ? null : operationHash,
            EndedAtUtc = active ? null : DateTimeOffset.UtcNow
        };
    }

    internal static UserMembershipAuthorization Authorization(
        Guid deviceId,
        Guid instanceId,
        long started,
        long? ended,
        bool active) => new()
    {
        AuthorizationId = Guid.NewGuid(),
        UserId = Guid.Empty,
        DeviceId = deviceId,
        OriginInstanceId = instanceId,
        StartedMembershipEpoch = started,
        EndedMembershipEpoch = ended,
        MinimumKeyEpoch = 1,
        MaximumKeyEpoch = null,
        IsActive = active
    };

    internal static UserRevisionKnowledge Knowledge(TombstoneCausalReference reference, long stored, long merged) => new()
    {
        UserId = Guid.Empty,
        OriginDeviceId = reference.OriginDeviceId,
        OriginInstanceId = reference.OriginInstanceId,
        UserKeyEpoch = reference.UserKeyEpoch,
        HighestStoredRevision = stored,
        HighestMergedRevision = merged
    };

    internal static UserSnapshotEnvelope Receipt(
        UserTombstoneGarbageCollectorMember member,
        long keyEpoch,
        TombstoneCausalReference? coverage) => new()
    {
        UserId = Guid.Empty,
        OriginDeviceId = member.DeviceId,
        OriginInstanceId = member.InstanceId,
        OriginRevision = 20,
        UserKeyEpoch = keyEpoch,
        MembershipEpoch = 2,
        Coverage = coverage is null
            ? []
            :
            [
                new UserSnapshotCoverageEntry
                {
                    OriginDeviceId = coverage.OriginDeviceId,
                    OriginInstanceId = coverage.OriginInstanceId,
                    UserKeyEpoch = coverage.UserKeyEpoch,
                    OriginRevision = coverage.OriginRevision
                }
            ]
    };


}
