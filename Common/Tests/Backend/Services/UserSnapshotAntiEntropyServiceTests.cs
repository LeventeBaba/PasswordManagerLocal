using Google.Protobuf;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using PasswordManagerLocal.Common.Backend.Constants;
using PasswordManagerLocal.Common.Backend.Services;
using PasswordManagerLocal.Common.Backend.Models;
using PasswordManagerLocal.Common.Tests.Fakes;
using PasswordManagerLocal.Common.Tests.TestInfrastructure;
using PasswordManagerLocal.Common.Backend.Sync;

using MSTestAssert = Microsoft.VisualStudio.TestTools.UnitTesting.Assert;

namespace PasswordManagerLocal.Common.Tests.Backend.Services;

[TestClass]
public sealed class UserSnapshotAntiEntropyServiceTests
{

    [TestMethod]
    [TestCategory("Backend")]
    [TestCategory("Integration")]
    public async Task BuildInventory_AdvertisesOnlyRetainedRelayableSnapshotsAndPreservesMergedAndForkKnowledge()
    {
        await using var database = await SqliteRepositoryTestDatabase.CreateAsync();
        var localDeviceId = Guid.NewGuid();
        var peerDeviceId = Guid.NewGuid();
        var retainedOriginId = Guid.NewGuid();
        var mergedOnlyOriginId = Guid.NewGuid();
        var quarantinedOriginId = Guid.NewGuid();
        var historicalOriginId = Guid.NewGuid();
        var olderReceiptOriginId = Guid.NewGuid();
        var user = new User
        {
            UId = Guid.NewGuid(),
            UsernameHash = [0x01],
            UsernameSalt = [0x02],
            PasswordSalt = [0x03],
            EncryptedPayload = [0x04],
            EncryptedGeneralUserDataPayload = [0x05],
            EncryptedUserPasswordsDataPayload = [0x06],
            EncryptedUserDevicesDataPayload = [0x07],
            KeyEpoch = 1,
            MembershipEpoch = 2
        };
        user.GenerateIntegrityHash();
        await database.Users.AddAsync(user);

        var localIdentityModel = new LocalDeviceIdentity
        {
            Id = localDeviceId,
            OriginInstanceId = Guid.NewGuid(),
            AgreementPrivateKeyBlob = [0x01],
            SignPrivateKeyBlob = [0x02],
            PFXCertificate = [0x03],
            DeviceType = DeviceType.WindowsPc,
            IsSyncOn = true
        };
        localIdentityModel.GenerateIntegrityHash();
        await database.DeviceIdentity.Create(localIdentityModel);
        var localLink = new LocalUserDevice
        {
            UserId = user.UId,
            LocalDeviceIdentityId = localDeviceId,
            IsSyncOn = true
        };
        localLink.GenerateIntegrityHash();
        await database.LocalUserDevices.AddAsync(localLink);

        foreach (var deviceId in new[] { peerDeviceId, retainedOriginId, mergedOnlyOriginId, quarantinedOriginId })
        {
            var device = new Device
            {
                Id = deviceId,
                PublicKey = Enumerable.Repeat((byte)0x11, 32).ToArray(),
                SignPublicKey = Enumerable.Repeat((byte)0x22, 32).ToArray(),
                TlsCertFingerprint = deviceId.ToString("N"),
                DeviceType = DeviceType.WindowsPc,
                IsTrusted = true,
                IsBlocked = false
            };
            device.GenerateIntegrityHash();
            await database.Devices.AddAsync(device);
            var link = new UserDevice
            {
                UserId = user.UId,
                DeviceId = deviceId,
                IsSyncOn = true,
                IsDeleted = false
            };
            link.GenerateIntegrityHash();
            await database.UserDevices.AddAsync(link);
        }

        var retainedInstance = Guid.NewGuid();
        var mergedOnlyInstance = Guid.NewGuid();
        var quarantinedInstance = Guid.NewGuid();
        var historicalInstance = Guid.NewGuid();
        var olderReceiptInstance = Guid.NewGuid();
        await database.UserRevisionKnowledge.AddAsync(Knowledge(user.UId, retainedOriginId, retainedInstance, stored: 16, merged: 10, marker: 0x16));
        await database.UserRevisionKnowledge.AddAsync(Knowledge(user.UId, mergedOnlyOriginId, mergedOnlyInstance, stored: 15, merged: 15, marker: 0x15));
        await database.UserRevisionKnowledge.AddAsync(Knowledge(user.UId, quarantinedOriginId, quarantinedInstance, stored: 9, merged: 8, marker: 0x31));
        await database.UserRevisionKnowledge.AddAsync(Knowledge(user.UId, historicalOriginId, historicalInstance, stored: 7, merged: 7, marker: 0x07));
        await database.UserRevisionKnowledge.AddAsync(Knowledge(user.UId, olderReceiptOriginId, olderReceiptInstance, stored: 20, merged: 20, marker: 0x20));
        await database.UserSyncSnapshots.AddAsync(Snapshot(user, retainedOriginId, retainedInstance, 16, UserSyncSnapshotStatus.MergedReceipt, 0x16));
        await database.UserSyncSnapshots.AddAsync(Snapshot(user, quarantinedOriginId, quarantinedInstance, 9, UserSyncSnapshotStatus.IsolatedFork, 0x31, 0x32));
        await database.UserSyncSnapshots.AddAsync(Snapshot(user, historicalOriginId, historicalInstance, 7, UserSyncSnapshotStatus.MergedReceipt, 0x07, membershipEpoch: 1));
        await database.UserSyncSnapshots.AddAsync(Snapshot(user, olderReceiptOriginId, olderReceiptInstance, 15, UserSyncSnapshotStatus.MergedReceipt, 0x15));
        await database.UnitOfWork.SaveChangesAsync();

        var service = new UserSnapshotAntiEntropyService(
            database.Users,
            database.UserDevices,
            database.LocalUserDevices,
            database.UserRevisionKnowledge,
            database.UserSyncSnapshots,
            database.Devices,
            new FakeOutgoingDeltaBuilderService(),
            new FakeDeviceIdentityService { LocalDeviceId = localDeviceId },
            new FakeUserMembershipAuthorizationService());

        var inventory = await service.BuildInventoryAsync(peerDeviceId);

        MSTestAssert.HasCount(1, inventory.Users);
        var entries = inventory.Users[0].Revisions.ToDictionary(entry => Guid.Parse(entry.OriginDeviceId));
        MSTestAssert.AreEqual(16L, entries[retainedOriginId].HighestStoredRevision);
        MSTestAssert.AreEqual(10L, entries[retainedOriginId].HighestMergedRevision);
        MSTestAssert.AreEqual(2L, entries[retainedOriginId].RetainedMembershipEpoch);
        MSTestAssert.AreEqual(7L, entries[historicalOriginId].HighestStoredRevision);
        MSTestAssert.AreEqual(7L, entries[historicalOriginId].HighestMergedRevision);
        MSTestAssert.AreEqual(1L, entries[historicalOriginId].RetainedMembershipEpoch);
        MSTestAssert.AreEqual(0L, entries[mergedOnlyOriginId].HighestStoredRevision);
        MSTestAssert.AreEqual(15L, entries[mergedOnlyOriginId].HighestMergedRevision);
        MSTestAssert.AreEqual(15L, entries[mergedOnlyOriginId].KnownSnapshotRevision);
        CollectionAssert.AreEqual(Hash(0x15), entries[mergedOnlyOriginId].KnownSnapshotHash.ToByteArray());
        MSTestAssert.AreEqual(15L, entries[olderReceiptOriginId].HighestStoredRevision);
        CollectionAssert.AreEqual(Hash(0x15), entries[olderReceiptOriginId].HighestStoredSnapshotHash.ToByteArray());
        MSTestAssert.AreEqual(20L, entries[olderReceiptOriginId].KnownSnapshotRevision);
        CollectionAssert.AreEqual(Hash(0x20), entries[olderReceiptOriginId].KnownSnapshotHash.ToByteArray());
        MSTestAssert.AreEqual(0L, entries[quarantinedOriginId].HighestStoredRevision);
        MSTestAssert.AreEqual(9L, entries[quarantinedOriginId].QuarantinedRevision);
        CollectionAssert.AreEqual(Hash(0x32), entries[quarantinedOriginId].ConflictingSnapshotHash.ToByteArray());
    }

    [TestMethod]
    [TestCategory("Backend")]
    public void FindMissingSnapshots_RequestsOnlyHigherStoredRevisionAndKeepsOriginInstancesSeparate()
    {
        var userId = Guid.NewGuid();
        var originA = Guid.NewGuid();
        var instanceX = Guid.NewGuid();
        var instanceY = Guid.NewGuid();
        var local = Inventory(userId, 1, 1,
            Revision(originA, instanceX, stored: 12, merged: 12, marker: 0x12));
        var remote = Inventory(userId, 1, 1,
            Revision(originA, instanceX, stored: 15, merged: 10, marker: 0x15),
            Revision(originA, instanceY, stored: 2, merged: 0, marker: 0x22));

        var requests = CreateService().FindMissingSnapshots(local, remote.Users);

        MSTestAssert.HasCount(2, requests);
        MSTestAssert.IsTrue(requests.Any(request =>
            Guid.Parse(request.OriginInstanceId) == instanceX && request.OriginRevision == 15));
        MSTestAssert.IsTrue(requests.Any(request =>
            Guid.Parse(request.OriginInstanceId) == instanceY && request.OriginRevision == 2));
    }

    [TestMethod]
    [TestCategory("Backend")]
    public void FindMissingSnapshots_DoesNotRequestSameOrLowerRetainedRevision()
    {
        var userId = Guid.NewGuid();
        var origin = Guid.NewGuid();
        var instance = Guid.NewGuid();
        var local = Inventory(userId, 1, 1,
            Revision(origin, instance, stored: 15, merged: 12, marker: 0x15));
        var same = Inventory(userId, 1, 1,
            Revision(origin, instance, stored: 15, merged: 10, marker: 0x15));
        var lower = Inventory(userId, 1, 1,
            Revision(origin, instance, stored: 12, merged: 10, marker: 0x12));

        var service = CreateService();

        MSTestAssert.HasCount(0, service.FindMissingSnapshots(local, same.Users));
        MSTestAssert.HasCount(0, service.FindMissingSnapshots(local, lower.Users));
    }

    [TestMethod]
    [TestCategory("Backend")]
    [DataRow(8L, 20L, 8L, 1)]
    [DataRow(15L, 20L, 15L, 0)]
    [DataRow(0L, 20L, 20L, 1)]
    [DataRow(0L, 8L, 20L, 1)]
    public void FindMissingSnapshots_RequiresRetainedEnvelopeToSuppressReceiptRequest(
        long retainedRevision, long mergedRevision, long knownRevision, int expectedRequestCount)
    {
        var userId = Guid.NewGuid();
        var origin = Guid.NewGuid();
        var instance = Guid.NewGuid();
        const long advertisedRevision = 15;
        const byte advertisedHashMarker = 0x15;
        var localRevision = Revision(origin, instance, stored: retainedRevision, merged: mergedRevision,
            marker: retainedRevision == advertisedRevision ? advertisedHashMarker : (byte)0x08);
        localRevision.KnownSnapshotRevision = knownRevision;
        localRevision.KnownSnapshotHash = retainedRevision > 0
            ? localRevision.HighestStoredSnapshotHash
            : ByteString.CopyFrom(Hash(0x20));
        var local = Inventory(userId, 1, 1, localRevision);
        var remote = Inventory(userId, 1, 1,
            Revision(origin, instance, stored: advertisedRevision, merged: 10, marker: advertisedHashMarker));

        var requests = CreateService().FindMissingSnapshots(local, remote.Users);

        MSTestAssert.HasCount(expectedRequestCount, requests);
        if (expectedRequestCount == 1)
        {
            MSTestAssert.AreEqual(advertisedRevision, requests[0].OriginRevision);
            CollectionAssert.AreEqual(Hash(advertisedHashMarker), requests[0].ExpectedSnapshotHash.ToByteArray());
        }
    }

    [TestMethod]
    [TestCategory("Backend")]
    public void FindMissingSnapshots_SameRevisionDifferentHashRequestsExactEnvelopeForForkDetection()
    {
        var userId = Guid.NewGuid();
        var origin = Guid.NewGuid();
        var instance = Guid.NewGuid();
        var local = Inventory(userId, 1, 1,
            Revision(origin, instance, stored: 9, merged: 8, marker: 0x31));
        var remote = Inventory(userId, 1, 1,
            Revision(origin, instance, stored: 9, merged: 8, marker: 0x32));

        var requests = CreateService().FindMissingSnapshots(local, remote.Users);

        MSTestAssert.HasCount(1, requests);
        MSTestAssert.AreEqual(9L, requests[0].OriginRevision);
        CollectionAssert.AreEqual(Hash(0x32), requests[0].ExpectedSnapshotHash.ToByteArray());
    }


    [TestMethod]
    [TestCategory("Backend")]
    public void FindMissingSnapshots_MergedRevisionStillRequestsConflictingExactHashForDurableForkEvidence()
    {
        var userId = Guid.NewGuid();
        var origin = Guid.NewGuid();
        var instance = Guid.NewGuid();
        var localRevision = Revision(origin, instance, stored: 0, merged: 9, marker: 0x00);
        localRevision.KnownSnapshotRevision = 9;
        localRevision.KnownSnapshotHash = ByteString.CopyFrom(Hash(0x31));
        var local = Inventory(userId, 1, 1, localRevision);
        var remote = Inventory(userId, 1, 1,
            Revision(origin, instance, stored: 9, merged: 8, marker: 0x32));

        var requests = CreateService().FindMissingSnapshots(local, remote.Users);

        MSTestAssert.HasCount(1, requests);
        MSTestAssert.AreEqual(9L, requests[0].OriginRevision);
        CollectionAssert.AreEqual(Hash(0x32), requests[0].ExpectedSnapshotHash.ToByteArray());
    }

    [TestMethod]
    [TestCategory("Backend")]
    public void FindMissingSnapshots_DoesNotRequestAnyNewerRevisionFromQuarantinedForkNamespace()
    {
        var userId = Guid.NewGuid();
        var origin = Guid.NewGuid();
        var instance = Guid.NewGuid();
        var localRevision = Revision(origin, instance, stored: 0, merged: 8, marker: 0x00);
        localRevision.QuarantinedRevision = 9;
        localRevision.QuarantinedSnapshotHash = ByteString.CopyFrom(Hash(0x31));
        localRevision.ConflictingSnapshotHash = ByteString.CopyFrom(Hash(0x32));
        var local = Inventory(userId, 1, 1, localRevision);
        var remote = Inventory(userId, 1, 1,
            Revision(origin, instance, stored: 10, merged: 8, marker: 0x33));

        var requests = CreateService().FindMissingSnapshots(local, remote.Users);

        MSTestAssert.HasCount(0, requests);
    }

    [TestMethod]
    [TestCategory("Backend")]
    public void FindMissingSnapshots_RequestsHistoricalReceiptUsingItsOriginalMembershipEpoch()
    {
        var userId = Guid.NewGuid();
        var origin = Guid.NewGuid();
        var instance = Guid.NewGuid();
        var local = Inventory(userId, 1, 3);
        var remoteRevision = Revision(origin, instance, stored: 4, merged: 4, marker: 0x04, retainedMembershipEpoch: 2);
        var remote = Inventory(userId, 1, 3, remoteRevision);

        var requests = CreateService().FindMissingSnapshots(local, remote.Users);

        MSTestAssert.HasCount(1, requests);
        MSTestAssert.AreEqual(2L, requests[0].MembershipEpoch);
    }


    [TestMethod]
    [TestCategory("Backend")]
    public void FindMissingSnapshots_RejectsDuplicateRemoteOriginNamespaces()
    {
        var userId = Guid.NewGuid();
        var origin = Guid.NewGuid();
        var instance = Guid.NewGuid();
        var local = Inventory(userId, 1, 1);
        var remote = Inventory(userId, 1, 1,
            Revision(origin, instance, stored: 9, merged: 8, marker: 0x31),
            Revision(origin, instance, stored: 10, merged: 8, marker: 0x32));

        try
        {
            _ = CreateService().FindMissingSnapshots(local, remote.Users);
            MSTestAssert.Fail("A duplicate remote origin namespace should be rejected.");
        }
        catch (InvalidDataException)
        {
        }
    }

    [TestMethod]
    [DataRow("matching", 0)]
    [DataRow("different-hash", 1)]
    [DataRow("missing-authored-envelope", 1)]
    [DataRow("different-transport-peer", 1)]
    [DataRow("fork", 1)]
    [DataRow("receipt-only", 1)]
    [TestCategory("Backend")]
    public void FindMissingSnapshots_UsesEquivalenceOnlyForAnExactRetainedAuthoredSnapshot(string scenario, int requestCount)
    {
        var userId = Guid.NewGuid();
        var identity = new FakeDeviceIdentityService();
        var peerId = Guid.NewGuid();
        var peerInstanceId = Guid.NewGuid();
        var authored = scenario == "missing-authored-envelope"
            ? Revision(identity.LocalDeviceId, identity.OriginInstanceId, stored: 0, merged: 5, marker: 0x00)
            : Revision(identity.LocalDeviceId, identity.OriginInstanceId, stored: 5, merged: 5, marker: 0x05);
        var advertised = Revision(peerId, peerInstanceId, stored: 10, merged: 10, marker: 0x10);
        advertised.EquivalentPeerSnapshotHash = ByteString.CopyFrom(Hash(scenario == "different-hash" ? (byte)0x04 : (byte)0x05));

        var local = Inventory(userId, 1, 1, authored);
        if (scenario == "fork")
            local.Users[0].Revisions.Add(Revision(peerId, peerInstanceId, stored: 10, merged: 9, marker: 0x11));
        if (scenario == "receipt-only")
            local.Users[0].Revisions.Add(Revision(peerId, peerInstanceId, stored: 0, merged: 10, marker: 0x00));

        var remote = Inventory(userId, 1, 1, advertised);
        var service = new UserSnapshotAntiEntropyService(null!, null!, null!, null!, null!, null!, null!, identity, null!);
        var authenticatedPeer = scenario == "different-transport-peer" ? Guid.NewGuid() : peerId;
        MSTestAssert.HasCount(requestCount, service.FindMissingSnapshots(local, remote.Users, authenticatedPeer));
    }

    private static UserRevisionKnowledge Knowledge(
        Guid userId,
        Guid originDeviceId,
        Guid originInstanceId,
        long stored,
        long merged,
        byte marker) =>
        new()
        {
            UserId = userId,
            OriginDeviceId = originDeviceId,
            OriginInstanceId = originInstanceId,
            UserKeyEpoch = 1,
            HighestStoredRevision = stored,
            HighestStoredSnapshotHash = stored == 0 ? [] : Hash(marker),
            HighestMergedRevision = merged
        };

    private static UserSyncSnapshot Snapshot(
        User user,
        Guid originDeviceId,
        Guid originInstanceId,
        long revision,
        UserSyncSnapshotStatus status,
        byte marker,
        byte? conflictingMarker = null,
        long? membershipEpoch = null) =>
        new()
        {
            UserId = user.UId,
            OriginDeviceId = originDeviceId,
            OriginInstanceId = originInstanceId,
            OriginRevision = revision,
            UserKeyEpoch = user.KeyEpoch,
            MembershipEpoch = membershipEpoch ?? user.MembershipEpoch,
            CreatedAtUtc = DateTimeOffset.UtcNow,
            ReceivedAtUtc = DateTimeOffset.UtcNow,
            SnapshotHash = Hash(marker),
            OriginSignPublicKey = new byte[32],
            OriginSignature = new byte[64],
            EnvelopePayload = [0x01],
            Status = status,
            ConflictingSnapshotHash = conflictingMarker.HasValue ? Hash(conflictingMarker.Value) : null
        };

    private static UserSnapshotAntiEntropyService CreateService() =>
        new(null!, null!, null!, null!, null!, null!, null!, null!, null!);

    private static UserSnapshotInventoryExchangeRequest Inventory(
        Guid userId,
        long keyEpoch,
        long membershipEpoch,
        params UserSnapshotRevisionInventory[] revisions)
    {
        var user = new UserSnapshotUserInventory
        {
            UserId = userId.ToString("N"),
            UserKeyEpoch = keyEpoch,
            MembershipEpoch = membershipEpoch
        };
        user.Revisions.AddRange(revisions);
        var inventory = new UserSnapshotInventoryExchangeRequest();
        inventory.Users.Add(user);
        return inventory;
    }

    private static UserSnapshotRevisionInventory Revision(
        Guid originDeviceId,
        Guid originInstanceId,
        long stored,
        long merged,
        byte marker,
        long retainedMembershipEpoch = 1) =>
        new()
        {
            OriginDeviceId = originDeviceId.ToString("N"),
            OriginInstanceId = originInstanceId.ToString("N"),
            UserKeyEpoch = 1,
            HighestStoredRevision = stored,
            HighestStoredSnapshotHash = stored == 0 ? ByteString.Empty : ByteString.CopyFrom(Hash(marker)),
            HighestMergedRevision = merged,
            KnownSnapshotRevision = stored,
            KnownSnapshotHash = stored == 0 ? ByteString.Empty : ByteString.CopyFrom(Hash(marker)),
            RetainedMembershipEpoch = stored == 0 ? 0 : retainedMembershipEpoch
        };

    private static byte[] Hash(byte marker) =>
        Enumerable.Repeat(marker, SyncConstants.SyncDeltaPayloadHashBytes).ToArray();
}
