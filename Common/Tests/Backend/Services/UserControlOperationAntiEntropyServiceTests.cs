using Google.Protobuf;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using PasswordManagerLocal.Common.Backend.Constants;
using PasswordManagerLocal.Common.Backend.Models;
using PasswordManagerLocal.Common.Backend.Services;
using PasswordManagerLocal.Common.Backend.Sync;

using MSTestAssert = Microsoft.VisualStudio.TestTools.UnitTesting.Assert;

namespace PasswordManagerLocal.Common.Tests.Backend.Services;

[TestClass]
public sealed class UserControlOperationAntiEntropyServiceTests
{
    [TestMethod]
    [TestCategory("Backend")]
    [TestCategory("Unit")]
    public void FindMissingOperations_RequestsOnlyMissingExactNonQuarantinedOperations()
    {
        var userId = Guid.NewGuid();
        var existing = CreateEntry(originSequence: 1, hashMarker: 0x11);
        var missing = CreateEntry(originSequence: 2, hashMarker: 0x22);
        var quarantined = CreateEntry(originSequence: 3, hashMarker: 0x33);
        quarantined.Quarantined = true;
        quarantined.ConflictingOperationHash = ByteString.CopyFrom(Enumerable.Repeat((byte)0x44, SyncConstants.SyncDeltaPayloadHashBytes).ToArray());

        var local = new UserControlOperationInventoryExchangeRequest();
        local.Users.Add(new UserControlOperationUserInventory { UserId = userId.ToString("N"), Operations = { existing } });
        var remote = new[]
        {
            new UserControlOperationUserInventory
            {
                UserId = userId.ToString("N"),
                Operations = { existing.Clone(), missing, quarantined }
            }
        };
        var service = new UserControlOperationAntiEntropyService(null!, null!, null!, null!, null!);

        var requests = service.FindMissingOperations(local, remote);

        MSTestAssert.HasCount(1, requests);
        MSTestAssert.AreEqual(missing.OperationId, requests[0].OperationId);
        MSTestAssert.AreEqual(missing.OriginSequence, requests[0].OriginSequence);
        CollectionAssert.AreEqual(missing.OperationHash.ToByteArray(), requests[0].ExpectedOperationHash.ToByteArray());
    }

    [TestMethod]
    [TestCategory("Backend")]
    [TestCategory("Unit")]
    public void FindMissingOperations_OrdersDependentMembershipAdditionsBeforeTheirSuccessors()
    {
        var userId = Guid.NewGuid();
        var earlier = CreateEntry(originSequence: 1, hashMarker: 0x41);
        earlier.OperationType = (int)UserControlOperationType.DeviceAddition;
        earlier.OriginDeviceId = "ffffffffffffffffffffffffffffffff";
        earlier.PreviousMembershipEpoch = 3;
        earlier.ResultingMembershipEpoch = 4;

        var later = CreateEntry(originSequence: 1, hashMarker: 0x42);
        later.OperationType = (int)UserControlOperationType.DeviceAddition;
        later.OriginDeviceId = "00000000000000000000000000000001";
        later.PreviousMembershipEpoch = 4;
        later.ResultingMembershipEpoch = 5;

        // The origin-ID order (and this inventory order) puts the successor first.
        // That successor cannot yet be authenticated by a device at membership epoch 3.
        var local = new UserControlOperationInventoryExchangeRequest();
        local.Users.Add(new UserControlOperationUserInventory { UserId = userId.ToString("N") });
        var remote = new UserControlOperationUserInventory
        {
            UserId = userId.ToString("N"),
            Operations = { later, earlier }
        };
        var service = new UserControlOperationAntiEntropyService(null!, null!, null!, null!, null!);

        var requests = service.FindMissingOperations(local, new[] { remote });

        CollectionAssert.AreEqual(new[] { earlier.OperationId, later.OperationId },
            requests.Select(request => request.OperationId).ToArray());
    }

    [TestMethod]
    [TestCategory("Backend")]
    [TestCategory("Unit")]
    public void FindMissingOperations_OrdersKeyEpochTransitionsBeforeDependentOperations()
    {
        var userId = Guid.NewGuid();
        var keyTransition = CreateEntry(originSequence: 1, hashMarker: 0x51);
        keyTransition.PreviousMembershipEpoch = 3;
        keyTransition.ResultingMembershipEpoch = 3;
        keyTransition.PreviousKeyEpoch = 1;
        keyTransition.ResultingKeyEpoch = 2;

        var dependentAddition = CreateEntry(originSequence: 2, hashMarker: 0x52);
        dependentAddition.OperationType = (int)UserControlOperationType.DeviceAddition;
        dependentAddition.PreviousMembershipEpoch = 3;
        dependentAddition.ResultingMembershipEpoch = 4;
        dependentAddition.PreviousKeyEpoch = 2;
        dependentAddition.ResultingKeyEpoch = 2;

        var local = new UserControlOperationInventoryExchangeRequest();
        local.Users.Add(new UserControlOperationUserInventory { UserId = userId.ToString("N") });
        var remote = new UserControlOperationUserInventory
        {
            UserId = userId.ToString("N"),
            Operations = { dependentAddition, keyTransition }
        };
        var service = new UserControlOperationAntiEntropyService(null!, null!, null!, null!, null!);

        var requests = service.FindMissingOperations(local, new[] { remote });

        CollectionAssert.AreEqual(new[] { keyTransition.OperationId, dependentAddition.OperationId },
            requests.Select(request => request.OperationId).ToArray());
    }

    [TestMethod]
    [TestCategory("Backend")]
    [TestCategory("Unit")]
    public void FindMissingOperations_RejectsDuplicateIdentityAndConflictingExactHash()
    {
        var userId = Guid.NewGuid();
        var remoteEntry = CreateEntry(originSequence: 1, hashMarker: 0x11);
        var service = new UserControlOperationAntiEntropyService(null!, null!, null!, null!, null!);
        var local = new UserControlOperationInventoryExchangeRequest();
        local.Users.Add(new UserControlOperationUserInventory { UserId = userId.ToString("N") });
        var duplicateUser = new UserControlOperationUserInventory
        {
            UserId = userId.ToString("N"),
            Operations = { remoteEntry, remoteEntry.Clone() }
        };

        ExpectThrows<InvalidDataException>(() => service.FindMissingOperations(local, new[] { duplicateUser }));

        var conflictingLocal = remoteEntry.Clone();
        conflictingLocal.OperationHash = ByteString.CopyFrom(Enumerable.Repeat((byte)0x99, SyncConstants.SyncDeltaPayloadHashBytes).ToArray());
        local.Users[0].Operations.Add(conflictingLocal);
        ExpectThrows<InvalidDataException>(() => service.FindMissingOperations(
            local,
            new[] { new UserControlOperationUserInventory { UserId = userId.ToString("N"), Operations = { remoteEntry } } }));
    }

    private static UserControlOperationInventoryEntry CreateEntry(long originSequence, byte hashMarker) =>
        new()
        {
            OperationId = Guid.NewGuid().ToString("N"),
            OriginDeviceId = Guid.NewGuid().ToString("N"),
            OriginInstanceId = Guid.NewGuid().ToString("N"),
            OriginSequence = originSequence,
            OperationType = (int)UserControlOperationType.KeyEpochReplacement,
            PreviousKeyEpoch = originSequence,
            ResultingKeyEpoch = originSequence + 1,
            PreviousMembershipEpoch = 1,
            ResultingMembershipEpoch = 1,
            OperationHash = ByteString.CopyFrom(Enumerable.Repeat(hashMarker, SyncConstants.SyncDeltaPayloadHashBytes).ToArray())
        };

    private static void ExpectThrows<TException>(Action action) where TException : Exception
    {
        try
        {
            action();
            MSTestAssert.Fail($"Expected exception: {typeof(TException).Name}");
        }
        catch (TException)
        {
        }
    }
}
