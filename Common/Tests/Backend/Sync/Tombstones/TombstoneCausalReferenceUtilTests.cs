using Microsoft.VisualStudio.TestTools.UnitTesting;
using PasswordManagerLocal.Common.Backend.Models.Encrypted;
using PasswordManagerLocal.Common.Backend.Sync.Tombstones;
using PasswordManagerLocal.Common.Backend.Utils;

using MSTestAssert = Microsoft.VisualStudio.TestTools.UnitTesting.Assert;

using PasswordManagerLocal.Common.Backend.Sync.Tombstones;

namespace PasswordManagerLocal.Common.Tests.Backend.Sync.Tombstones;

[TestClass]
public sealed class TombstoneCausalReferenceUtilTests
{
    [TestMethod]
    [TestCategory("Backend")]
    public void AssignAndValidate_LocalDeletion_AssignsNextSnapshotWithoutChangingItemVersion()
    {
        var deviceId = Guid.NewGuid();
        var instanceId = Guid.NewGuid();
        var version = Stamp(deviceId, instanceId, 4);
        using var bundle = new UserDataBundle();
        bundle.UserPasswordsData.DeletedPasswords.Add(new DeletedPasswordData
        {
            Id = Guid.NewGuid(),
            Version = version
        });

        var changed = TombstoneCausalReferenceUtil.AssignAndValidate(
            bundle, deviceId, instanceId, 3, 7, 19, UserDataBlobKind.Passwords);

        var tombstone = bundle.UserPasswordsData.DeletedPasswords.Single();
        MSTestAssert.AreEqual(UserDataBlobKind.Passwords, changed);
        MSTestAssert.AreEqual(version, tombstone.Version);
        MSTestAssert.AreEqual(deviceId, tombstone.CausalReference.OriginDeviceId);
        MSTestAssert.AreEqual(instanceId, tombstone.CausalReference.OriginInstanceId);
        MSTestAssert.AreEqual(3L, tombstone.CausalReference.UserKeyEpoch);
        MSTestAssert.AreEqual(7L, tombstone.CausalReference.MembershipEpoch);
        MSTestAssert.AreEqual(19L, tombstone.CausalReference.OriginRevision);
        MSTestAssert.IsTrue(tombstone.IsIntegrityValid());
    }

    [TestMethod]
    [TestCategory("Backend")]
    public void AssignAndValidate_SecondRun_IsIdempotent()
    {
        var deviceId = Guid.NewGuid();
        var instanceId = Guid.NewGuid();
        using var bundle = new UserDataBundle();
        bundle.UserPasswordsData.DeletedTags.Add(new DeletedPasswordTagData
        {
            Id = Guid.NewGuid(),
            Version = Stamp(deviceId, instanceId, 0)
        });

        TombstoneCausalReferenceUtil.AssignAndValidate(
            bundle, deviceId, instanceId, 1, 1, 5, UserDataBlobKind.Passwords);
        var firstReference = bundle.UserPasswordsData.DeletedTags.Single().CausalReference;
        var firstHash = bundle.UserPasswordsData.DeletedTags.Single().IntegrityHash.ToArray();

        var changed = TombstoneCausalReferenceUtil.AssignAndValidate(
            bundle, deviceId, instanceId, 1, 1, 6, UserDataBlobKind.Passwords);

        MSTestAssert.AreEqual(UserDataBlobKind.None, changed);
        MSTestAssert.AreEqual(firstReference, bundle.UserPasswordsData.DeletedTags.Single().CausalReference);
        CollectionAssert.AreEqual(firstHash, bundle.UserPasswordsData.DeletedTags.Single().IntegrityHash);
    }

    [TestMethod]
    [TestCategory("Backend")]
    public void AssignAndValidate_UnanchoredRemoteDeletion_FailsClosed()
    {
        var localDeviceId = Guid.NewGuid();
        var localInstanceId = Guid.NewGuid();
        using var bundle = new UserDataBundle();
        bundle.UserDevicesData.DeletedDevices.Add(new DeletedUserDeviceData
        {
            Id = Guid.NewGuid(),
            Version = Stamp(Guid.NewGuid(), Guid.NewGuid(), 0)
        });

        MSTestAssert.ThrowsExactly<InvalidDataException>(() =>
            TombstoneCausalReferenceUtil.AssignAndValidate(
                bundle, localDeviceId, localInstanceId, 1, 1, 1, UserDataBlobKind.Devices));
    }

    [TestMethod]
    [TestCategory("Backend")]
    public void Validate_MismatchedDeletionOrigin_FailsClosed()
    {
        var version = Stamp(Guid.NewGuid(), Guid.NewGuid(), 0);
        var reference = new TombstoneCausalReference
        {
            OriginDeviceId = Guid.NewGuid(),
            OriginInstanceId = version.OriginInstanceId,
            UserKeyEpoch = 1,
            MembershipEpoch = 1,
            OriginRevision = 1
        };

        MSTestAssert.ThrowsExactly<InvalidDataException>(() =>
            TombstoneCausalReferenceUtil.Validate(version, reference));
    }

    private static SyncVersionStamp Stamp(Guid deviceId, Guid instanceId, long logical) => new()
    {
        PhysicalTimeUnixMilliseconds = 1_700_000_000_000,
        LogicalCounter = logical,
        OriginDeviceId = deviceId,
        OriginInstanceId = instanceId
    };
}
