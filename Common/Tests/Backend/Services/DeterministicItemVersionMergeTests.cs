using Microsoft.VisualStudio.TestTools.UnitTesting;
using PasswordManagerLocal.Common.Backend.Exceptions;
using PasswordManagerLocal.Common.Backend.Models;
using PasswordManagerLocal.Common.Backend.Models.Encrypted;
using PasswordManagerLocal.Common.Backend.Services;
using PasswordManagerLocal.Common.Backend.Sync;
using PasswordManagerLocal.Common.Backend.Utils;
using System.Text.Json;

using MSTestAssert = Microsoft.VisualStudio.TestTools.UnitTesting.Assert;

using PasswordManagerLocal.Common.Tests.TestInfrastructure.Services.Fixtures;

using PasswordManagerLocal.Common.Backend.Sync.Enrollment;
namespace PasswordManagerLocal.Common.Tests.Backend.Services;

[TestClass]
public sealed class DeterministicItemVersionMergeTests
{
    private static readonly Guid DeviceA = Guid.Parse("10000000-0000-0000-0000-000000000001");
    private static readonly Guid DeviceB = Guid.Parse("20000000-0000-0000-0000-000000000002");
    private static readonly Guid InstanceA = Guid.Parse("30000000-0000-0000-0000-000000000003");
    private static readonly Guid InstanceB = Guid.Parse("40000000-0000-0000-0000-000000000004");

    [TestMethod]
    public void VersionComparison_UsesEveryFieldAndRejectsInvalidStamps()
    {
        var physical = Stamp(100, 0, DeviceA, InstanceA);
        var logical = Stamp(100, 1, DeviceA, InstanceA);
        var device = Stamp(100, 1, DeviceB, InstanceA);
        var instance = Stamp(100, 1, DeviceB, InstanceB);

        MSTestAssert.IsTrue(SyncVersionStampComparer.Instance.Compare(physical, logical) < 0);
        MSTestAssert.IsTrue(SyncVersionStampComparer.Instance.Compare(logical, device) < 0);
        MSTestAssert.IsTrue(SyncVersionStampComparer.Instance.Compare(device, instance) < 0);
        MSTestAssert.AreEqual(0, SyncVersionStampComparer.Instance.Compare(instance, instance));
        MSTestAssert.AreEqual(
            -SyncVersionStampComparer.Instance.Compare(instance, physical),
            SyncVersionStampComparer.Instance.Compare(physical, instance));
        MSTestAssert.IsTrue(SyncVersionStampComparer.Instance.Compare(physical, device) < 0);
        MSTestAssert.Throws<InvalidDataException>(() =>
            SyncVersionStampComparer.Instance.Compare(new SyncVersionStamp(), physical));
        MSTestAssert.Throws<InvalidDataException>(() =>
            SyncVersionStampComparer.Instance.Equals(new SyncVersionStamp(), physical));
    }

    [TestMethod]
    public void GeneralUserDataMerge_IsCommutativeAndUsesVersionInsteadOfDisplayTime()
    {
        var userId = Guid.NewGuid();
        var aVersion = Stamp(500, 0, DeviceA, InstanceA);
        var bVersion = Stamp(500, 0, DeviceB, InstanceB);
        using var a = General("A", DateTime.UnixEpoch.AddYears(2), aVersion);
        using var b = General("B", DateTime.UnixEpoch, bVersion);
        b.RegistrationTimeZoneId = "Europe/Budapest";
        b.RegistrationDeviceType = DeviceType.AndroidMobile;
        b.GenerateIntegrityHash();
        var aHash = new byte[] { 1, 2, 3 };
        var aSalt = new byte[] { 4, 5, 6 };
        var bHash = new byte[] { 7, 8, 9 };
        var bSalt = new byte[] { 10, 11, 12 };

        using var forwardData = General("A", a.LastUpdatedAt, aVersion);
        using var reverseData = Clone(b);
        var forwardUser = new User { UId = userId, UsernameHash = aHash.ToArray(), UsernameSalt = aSalt.ToArray() };
        var reverseUser = new User { UId = userId, UsernameHash = bHash.ToArray(), UsernameSalt = bSalt.ToArray() };
        var aPayload = new UserSyncPayload { UId = userId, UsernameHash = aHash, UsernameSalt = aSalt };
        var bPayload = new UserSyncPayload { UId = userId, UsernameHash = bHash, UsernameSalt = bSalt };

        MSTestAssert.IsTrue(GeneralUserDataMergeUtil.Merge(forwardData, b, forwardUser, bPayload));
        MSTestAssert.IsFalse(GeneralUserDataMergeUtil.Merge(reverseData, a, reverseUser, aPayload));

        CollectionAssert.AreEqual(forwardData.CalculateIntegrityHash(), reverseData.CalculateIntegrityHash());
        CollectionAssert.AreEqual(forwardUser.UsernameHash, reverseUser.UsernameHash);
        CollectionAssert.AreEqual(forwardUser.UsernameSalt, reverseUser.UsernameSalt);
        MSTestAssert.AreEqual("B", forwardData.Username);
        MSTestAssert.AreEqual("Europe/Budapest", forwardData.RegistrationTimeZoneId);
        MSTestAssert.AreEqual(DeviceType.AndroidMobile, forwardData.RegistrationDeviceType);
    }

    [TestMethod]
    public void GeneralUserDataMerge_ExactVersionWithDifferentContentFailsClosed()
    {
        var userId = Guid.NewGuid();
        var version = Stamp(510, 0, DeviceA, InstanceA);
        using var first = General("first", DateTime.UnixEpoch, version);
        using var second = General("second", DateTime.UnixEpoch, version);
        var user = new User { UId = userId, UsernameHash = [1], UsernameSalt = [2] };
        var incoming = new UserSyncPayload { UId = userId, UsernameHash = [1], UsernameSalt = [2] };

        MSTestAssert.Throws<DeterministicSyncConflictException>(() =>
            GeneralUserDataMergeUtil.Merge(first, second, user, incoming));
    }

    [TestMethod]
    public void PasswordMerge_IsCommutativeIdempotentAndUsesVersionInsteadOfDisplayTime()
    {
        var id = Guid.NewGuid();
        var olderVersion = Stamp(1_000, 0, DeviceA, InstanceA);
        var newerVersion = Stamp(1_000, 0, DeviceB, InstanceB);
        using var a = Passwords(password: Password(id, "A", DateTime.UnixEpoch.AddDays(10), olderVersion));
        using var b = Passwords(password: Password(id, "B", DateTime.UnixEpoch, newerVersion));

        using var ab = Clone(a);
        using var ba = Clone(b);
        var service = new UserPasswordsDataMergeService();
        MSTestAssert.IsTrue(service.Merge(ab, b));
        // ba already contains the winning version, so merging the older state is a no-op.
        MSTestAssert.IsFalse(service.Merge(ba, a));
        MSTestAssert.AreEqual("B", ab.Passwords.Single().Name);
        MSTestAssert.AreEqual("B", ba.Passwords.Single().Name);
        CollectionAssert.AreEqual(ab.CalculateIntegrityHash(), ba.CalculateIntegrityHash());
        MSTestAssert.IsFalse(service.Merge(ab, ba));
    }

    [TestMethod]
    public void PasswordMerge_IsAssociativeAcrossUpdateAndDeletion()
    {
        var id = Guid.NewGuid();
        using var a = Passwords(password: Password(id, "A", DateTime.UtcNow, Stamp(10, 0, DeviceA, InstanceA)));
        using var b = Passwords(deleted: DeletedPassword(id, Stamp(11, 0, DeviceB, InstanceA)));
        using var c = Passwords(password: Password(id, "C", DateTime.UtcNow, Stamp(12, 0, DeviceA, InstanceB)));
        var service = new UserPasswordsDataMergeService();

        using var left = Clone(a);
        service.Merge(left, b);
        service.Merge(left, c);

        using var bc = Clone(b);
        service.Merge(bc, c);
        using var right = Clone(a);
        service.Merge(right, bc);

        CollectionAssert.AreEqual(left.CalculateIntegrityHash(), right.CalculateIntegrityHash());
        MSTestAssert.AreEqual("C", left.Passwords.Single().Name);
        MSTestAssert.AreEqual(left.Passwords.Single().Version, right.Passwords.Single().Version);
    }

    [TestMethod]
    public void PasswordMerge_NewerDeletionWinsAndNewerLiveMutationRestores()
    {
        var id = Guid.NewGuid();
        var service = new UserPasswordsDataMergeService();
        using var live = Passwords(password: Password(id, "live", DateTime.UtcNow, Stamp(10, 0, DeviceA, InstanceA)));
        using var deleted = Passwords(deleted: DeletedPassword(id, Stamp(11, 0, DeviceB, InstanceB)));
        MSTestAssert.IsTrue(service.Merge(live, deleted));
        MSTestAssert.IsEmpty(live.Passwords);
        MSTestAssert.HasCount(1, live.DeletedPasswords);

        using var restored = Passwords(password: Password(id, "restored", DateTime.UtcNow, Stamp(12, 0, DeviceA, InstanceA)));
        MSTestAssert.IsTrue(service.Merge(live, restored));
        MSTestAssert.AreEqual("restored", live.Passwords.Single().Name);
        MSTestAssert.IsEmpty(live.DeletedPasswords);
    }

    [TestMethod]
    public void PasswordMerge_ExactVersionWithDifferentContentFailsClosed()
    {
        var id = Guid.NewGuid();
        var version = Stamp(10, 3, DeviceA, InstanceA);
        using var first = Passwords(password: Password(id, "one", DateTime.UtcNow, version));
        using var second = Passwords(password: Password(id, "two", DateTime.UtcNow, version));

        MSTestAssert.Throws<DeterministicSyncConflictException>(() =>
            new UserPasswordsDataMergeService().Merge(first, second));
    }

    [TestMethod]
    public void TagAndColorMerge_UseTheSameVersionAndDeletionRules()
    {
        var tagId = Guid.NewGuid();
        var colorId = Guid.NewGuid();
        using var first = Passwords(
            tag: Tag(tagId, "old", Stamp(20, 0, DeviceA, InstanceA)),
            color: Color(colorId, "old", Stamp(20, 0, DeviceA, InstanceA)));
        using var second = Passwords(
            tag: Tag(tagId, "new", Stamp(20, 1, DeviceA, InstanceA)),
            deletedColor: DeletedColor(colorId, Stamp(21, 0, DeviceB, InstanceB)));
        var service = new UserPasswordsDataMergeService();

        using var forward = Clone(first);
        using var reverse = Clone(second);
        service.Merge(forward, second);
        service.Merge(reverse, first);

        MSTestAssert.AreEqual("new", forward.Tags.Single().Name);
        MSTestAssert.IsEmpty(forward.CustomColors);
        MSTestAssert.HasCount(1, forward.DeletedCustomColors);
        CollectionAssert.AreEqual(forward.CalculateIntegrityHash(), reverse.CalculateIntegrityHash());
    }

    [TestMethod]
    public void TagDeletionReferenceCleanup_IsDerivedAndDoesNotRewritePasswordVersion()
    {
        var passwordId = Guid.NewGuid();
        var tagId = Guid.NewGuid();
        var passwordVersion = Stamp(10, 0, DeviceA, InstanceA);
        using var local = Passwords(
            password: Password(passwordId, "password", DateTime.UtcNow, passwordVersion, tagId),
            tag: Tag(tagId, "tag", Stamp(9, 0, DeviceA, InstanceA)));
        using var incoming = Passwords(deletedTag: DeletedTag(tagId, Stamp(11, 0, DeviceB, InstanceB)));

        new UserPasswordsDataMergeService().Merge(local, incoming);

        MSTestAssert.AreEqual(passwordVersion, local.Passwords.Single().Version);
        CollectionAssert.AreEqual(new[] { tagId }, local.Passwords.Single().TagIds);
        var response = new PasswordService().ConvertToPasswordInfoResponses(local).Single();
        MSTestAssert.IsEmpty(response.TagIds);
    }

    [TestMethod]
    public void PasswordMerge_AllThreeWayPermutationsProduceTheSameCanonicalResult()
    {
        var sharedPasswordId = Guid.Parse("50000000-0000-0000-0000-000000000005");
        var independentPasswordId = Guid.Parse("60000000-0000-0000-0000-000000000006");
        var tagId = Guid.Parse("70000000-0000-0000-0000-000000000007");
        var colorId = Guid.Parse("80000000-0000-0000-0000-000000000008");
        using var a = Passwords(
            password: Password(sharedPasswordId, "A", DateTime.UnixEpoch, Stamp(60, 0, DeviceA, InstanceA), tagId),
            tag: Tag(tagId, "tag", Stamp(60, 1, DeviceA, InstanceA)),
            color: Color(colorId, "color-a", Stamp(60, 2, DeviceA, InstanceA)));
        using var b = Passwords(
            password: Password(sharedPasswordId, "B", DateTime.UnixEpoch.AddYears(1), Stamp(61, 0, DeviceB, InstanceA), tagId),
            deletedTag: DeletedTag(tagId, Stamp(62, 0, DeviceB, InstanceA)),
            deletedColor: DeletedColor(colorId, Stamp(61, 1, DeviceB, InstanceA)));
        using var c = Passwords(
            password: Password(independentPasswordId, "C", DateTime.UnixEpoch, Stamp(63, 0, DeviceA, InstanceB)),
            color: Color(colorId, "color-c", Stamp(64, 0, DeviceA, InstanceB)));

        var sources = new[] { a, b, c };
        var hashes = new List<byte[]>();
        foreach (var permutation in Permutations(sources))
        {
            using var result = Clone(permutation[0]);
            var service = new UserPasswordsDataMergeService();
            service.Merge(result, permutation[1]);
            service.Merge(result, permutation[2]);
            hashes.Add(result.CalculateIntegrityHash());
            MSTestAssert.IsFalse(service.Merge(result, permutation[0]));
        }

        foreach (var hash in hashes.Skip(1))
            CollectionAssert.AreEqual(hashes[0], hash);
    }

    [TestMethod]
    public void FullLogicalBundleMerge_AllPermutationsConvergeAndRepeatedDeliveryIsANoOp()
    {
        var sharedPasswordId = Guid.Parse("d0000000-0000-0000-0000-00000000000d");
        var independentPasswordId = Guid.Parse("e0000000-0000-0000-0000-00000000000e");
        var tagId = Guid.Parse("f0000000-0000-0000-0000-00000000000f");
        var colorId = Guid.Parse("01000000-0000-0000-0000-000000000010");
        var encryptedDeviceId = Guid.Parse("02000000-0000-0000-0000-000000000020");
        using var a = LogicalBundleState.Create(
            General("alpha", DateTime.UnixEpoch.AddYears(5), Stamp(80, 0, DeviceA, InstanceA)),
            Passwords(
                password: Password(sharedPasswordId, "A", DateTime.UnixEpoch.AddYears(5), Stamp(80, 1, DeviceA, InstanceA), tagId),
                tag: Tag(tagId, "tag", Stamp(80, 2, DeviceA, InstanceA)),
                color: Color(colorId, "color-a", Stamp(80, 3, DeviceA, InstanceA))),
            Devices(device: UserDevice(encryptedDeviceId, "device-a", Stamp(80, 4, DeviceA, InstanceA), 1)),
            1);
        using var b = LogicalBundleState.Create(
            General("beta", DateTime.UnixEpoch, Stamp(81, 0, DeviceB, InstanceA)),
            Passwords(
                password: Password(sharedPasswordId, "B", DateTime.UnixEpoch, Stamp(81, 1, DeviceB, InstanceA), tagId),
                deletedTag: DeletedTag(tagId, Stamp(81, 2, DeviceB, InstanceA)),
                deletedColor: DeletedColor(colorId, Stamp(81, 3, DeviceB, InstanceA))),
            Devices(deleted: DeletedDevice(encryptedDeviceId, Stamp(81, 4, DeviceB, InstanceA))),
            2);
        using var c = LogicalBundleState.Create(
            General("gamma", DateTime.UnixEpoch.AddYears(-1), Stamp(82, 0, DeviceA, InstanceB)),
            Passwords(
                password: Password(independentPasswordId, "C", DateTime.UnixEpoch, Stamp(82, 1, DeviceA, InstanceB)),
                color: Color(colorId, "color-c", Stamp(82, 2, DeviceA, InstanceB))),
            Devices(device: UserDevice(encryptedDeviceId, "device-c", Stamp(82, 3, DeviceA, InstanceB), 3)),
            3);

        var fingerprints = new List<string>();
        foreach (var permutation in Permutations(new[] { a, b, c }))
        {
            using var result = permutation[0].Clone();
            result.MergeFrom(permutation[1]);
            result.MergeFrom(permutation[2]);
            fingerprints.Add(result.Fingerprint());
            MSTestAssert.IsFalse(result.MergeFrom(a));
            MSTestAssert.IsFalse(result.MergeFrom(b));
            MSTestAssert.IsFalse(result.MergeFrom(c));
        }

        foreach (var fingerprint in fingerprints.Skip(1))
            MSTestAssert.AreEqual(fingerprints[0], fingerprint);
    }

    [TestMethod]
    public void PasswordMerge_CanonicalizesUnorderedTagIdsWithoutChangingVersion()
    {
        var firstTag = Guid.Parse("90000000-0000-0000-0000-000000000009");
        var secondTag = Guid.Parse("a0000000-0000-0000-0000-00000000000a");
        var version = Stamp(70, 0, DeviceA, InstanceA);
        using var local = Passwords(password: Password(Guid.NewGuid(), "password", DateTime.UnixEpoch, version, secondTag, firstTag));
        using var incoming = Clone(local);

        MSTestAssert.IsTrue(new UserPasswordsDataMergeService().Merge(local, incoming));
        CollectionAssert.AreEqual(new[] { firstTag, secondTag }.Order().ToArray(), local.Passwords.Single().TagIds);
        MSTestAssert.AreEqual(version, local.Passwords.Single().Version);
    }

    [TestMethod]
    public void UserDeviceMerge_IsAtomicCommutativeAssociativeAndDeletionAware()
    {
        var id = Guid.NewGuid();
        using var a = Devices(device: UserDevice(id, "A", Stamp(30, 0, DeviceA, InstanceA), 1));
        using var b = Devices(deleted: DeletedDevice(id, Stamp(31, 0, DeviceB, InstanceA)));
        using var c = Devices(device: UserDevice(id, "C", Stamp(32, 0, DeviceA, InstanceB), 3));
        var service = new UserDevicesDataMergeService();

        using var left = Clone(a);
        service.Merge(left, b);
        service.Merge(left, c);

        using var bc = Clone(b);
        service.Merge(bc, c);
        using var right = Clone(a);
        service.Merge(right, bc);

        MSTestAssert.AreEqual("C", left.Devices.Single().Name);
        MSTestAssert.AreEqual(3, left.Devices.Single().LastLoginDate.Day);
        MSTestAssert.AreEqual(2, left.Devices.Single().PreviousLoginDate?.Day);
        CollectionAssert.AreEqual(left.CalculateIntegrityHash(), right.CalculateIntegrityHash());
        MSTestAssert.IsFalse(service.Merge(left, right));
    }

    [TestMethod]
    public void UserDeviceDuplicateNames_AreResolvedAsADeterministicDerivedView()
    {
        var olderId = Guid.Parse("b0000000-0000-0000-0000-00000000000b");
        var newerId = Guid.Parse("c0000000-0000-0000-0000-00000000000c");
        var olderVersion = Stamp(35, 0, DeviceA, InstanceA);
        var newerVersion = Stamp(36, 0, DeviceB, InstanceB);
        using var first = Devices(device: UserDevice(olderId, "Shared", olderVersion, 1));
        using var second = Devices(device: UserDevice(newerId, "Shared", newerVersion, 2));
        using var forward = Clone(first);
        using var reverse = Clone(second);
        var service = new UserDevicesDataMergeService();

        service.Merge(forward, second);
        service.Merge(reverse, first);
        var names = UserDevicePresentationUtil.ResolveNames(forward.Devices);

        CollectionAssert.AreEqual(forward.CalculateIntegrityHash(), reverse.CalculateIntegrityHash());
        MSTestAssert.AreEqual("Shared", names[newerId]);
        MSTestAssert.AreNotEqual("Shared", names[olderId]);
        MSTestAssert.AreEqual(olderVersion, forward.Devices.Single(device => device.Id == olderId).Version);
        MSTestAssert.AreEqual(newerVersion, forward.Devices.Single(device => device.Id == newerId).Version);
        MSTestAssert.IsTrue(forward.Devices.All(device => device.Name == "Shared"));
    }

    [TestMethod]
    public void UserDeviceMerge_ExactVersionWithDifferentContentFailsClosed()
    {
        var id = Guid.NewGuid();
        var version = Stamp(40, 0, DeviceA, InstanceA);
        using var first = Devices(device: UserDevice(id, "one", version, 1));
        using var second = Devices(device: UserDevice(id, "two", version, 2));

        MSTestAssert.Throws<DeterministicSyncConflictException>(() =>
            new UserDevicesDataMergeService().Merge(first, second));
    }

    [TestMethod]
    public void ReencryptionKeyReplacement_DoesNotChangeLogicalItemVersions()
    {
        var generalVersion = Stamp(45, 0, DeviceA, InstanceA);
        var passwordVersion = Stamp(45, 1, DeviceA, InstanceA);
        var deviceVersion = Stamp(45, 2, DeviceA, InstanceA);
        using var bundle = new UserDataBundle
        {
            UserData = new UserData { UId = Guid.NewGuid() },
            GeneralUserData = new GeneralUserData { Username = "user", Version = generalVersion },
            UserPasswordsData = Passwords(password: Password(Guid.NewGuid(), "password", DateTime.UtcNow, passwordVersion)),
            UserDevicesData = Devices(device: UserDevice(Guid.NewGuid(), "device", deviceVersion, 1))
        };
        UserDataKeyUtil.InitializeUserDataKeys(bundle.UserData);

        UserDataKeyUtil.ReplaceUserBlobKeys(bundle.UserData);

        MSTestAssert.AreEqual(generalVersion, bundle.GeneralUserData.Version);
        MSTestAssert.AreEqual(passwordVersion, bundle.UserPasswordsData.Passwords.Single().Version);
        MSTestAssert.AreEqual(deviceVersion, bundle.UserDevicesData.Devices.Single().Version);
    }

    [TestMethod]
    public void EnrollmentSnapshotSerialization_PreservesVersionedEncryptedPayloadBytes()
    {
        var userId = Guid.NewGuid();
        var version = Stamp(49, 3, DeviceA, InstanceB);
        using var passwords = Passwords(password: Password(Guid.NewGuid(), "enrollment", DateTime.UtcNow, version));
        var encryptedPayloadBytes = JsonSerializer.SerializeToUtf8Bytes(
            passwords,
            BackendJsonSerializerContext.Default.UserPasswordsData);
        var snapshot = new DeviceEnrollmentSnapshot
        {
            PrimaryUserId = userId,
            Users =
            [
                new DeviceEnrollmentUserSnapshot
                {
                    UId = userId,
                    GeneralUserDataVersion = version,
                    KeyEpoch = 1,
                    MembershipEpoch = 1,
                    EncryptedUserPasswordsDataPayload = encryptedPayloadBytes
                }
            ]
        };

        var serialized = JsonSerializer.SerializeToUtf8Bytes(
            snapshot,
            BackendJsonSerializerContext.Default.DeviceEnrollmentSnapshot);
        var restoredSnapshot = JsonSerializer.Deserialize(
            serialized,
            BackendJsonSerializerContext.Default.DeviceEnrollmentSnapshot)
            ?? throw new AssertFailedException("The enrollment snapshot did not deserialize.");
        CollectionAssert.AreEqual(
            encryptedPayloadBytes,
            restoredSnapshot.Users.Single().EncryptedUserPasswordsDataPayload);
        MSTestAssert.AreEqual(version, restoredSnapshot.Users.Single().GeneralUserDataVersion);

        using var restoredPasswords = JsonSerializer.Deserialize(
            restoredSnapshot.Users.Single().EncryptedUserPasswordsDataPayload,
            BackendJsonSerializerContext.Default.UserPasswordsData)
            ?? throw new AssertFailedException("The versioned enrollment payload did not deserialize.");
        MSTestAssert.AreEqual(version, restoredPasswords.Passwords.Single().Version);
    }

    [TestMethod]
    public void IntegrityAndSerialization_AuthenticateAndPreserveVersionStamps()
    {
        var id = Guid.NewGuid();
        var version = Stamp(50, 7, DeviceB, InstanceB);
        using var source = Passwords(password: Password(id, "serialized", DateTime.UtcNow, version));
        var json = JsonSerializer.Serialize(source, BackendJsonSerializerContext.Default.UserPasswordsData);
        using var restored = JsonSerializer.Deserialize(json, BackendJsonSerializerContext.Default.UserPasswordsData)
            ?? throw new AssertFailedException("The versioned password payload did not deserialize.");
        MSTestAssert.AreEqual(version, restored.Passwords.Single().Version);
        new UserDataBundleIntegrityService().VerifyUserPasswordsData(restored);

        restored.Passwords.Single().Version = Stamp(51, 0, DeviceA, InstanceA);
        MSTestAssert.Throws<InvalidDataIntegrityException>(() =>
            new UserDataBundleIntegrityService().VerifyUserPasswordsData(restored));

        restored.Passwords.Single().Version = new SyncVersionStamp();
        restored.Passwords.Single().GenerateIntegrityHash();
        restored.GenerateIntegrityHash();
        MSTestAssert.Throws<InvalidDataException>(() =>
            new UserDataBundleIntegrityService().VerifyUserPasswordsData(restored));
    }

    internal static GeneralUserData General(string username, DateTime displayTime, SyncVersionStamp version)
    {
        var data = new GeneralUserData
        {
            Username = username,
            FirstName = username,
            LastName = "User",
            Email = $"{username.ToLowerInvariant()}@example.test",
            RegistrationDate = DateTime.UnixEpoch,
            RegistrationTimeZoneId = "UTC",
            RegistrationDeviceType = DeviceType.WindowsPc,
            LastUpdatedAt = displayTime,
            Version = version
        };
        data.GenerateIntegrityHash();
        return data;
    }

    private static SyncVersionStamp Stamp(long physical, long logical, Guid device, Guid instance) => new()
    {
        PhysicalTimeUnixMilliseconds = physical,
        LogicalCounter = logical,
        OriginDeviceId = device,
        OriginInstanceId = instance
    };

    private static SecurePassword Password(Guid id, string name, DateTime displayTime, SyncVersionStamp version, params Guid[] tags)
    {
        var item = new SecurePassword
        {
            Id = id,
            Name = name,
            Description = string.Empty,
            Color = "#000000",
            Password = [1, 2, 3],
            TagIds = tags.ToList(),
            CreatedAt = displayTime,
            LastUpdatedAt = displayTime,
            Version = version
        };
        item.GenerateIntegrityHash();
        return item;
    }

    private static PasswordTag Tag(Guid id, string name, SyncVersionStamp version)
    {
        var item = new PasswordTag
        {
            Id = id,
            Name = name,
            Color = "#000000",
            LastUpdatedAt = DateTime.UtcNow,
            Version = version
        };
        item.GenerateIntegrityHash();
        return item;
    }

    private static CustomUserColor Color(Guid id, string name, SyncVersionStamp version)
    {
        var item = new CustomUserColor
        {
            Id = id,
            ColorName = name,
            ColorCode = "#000000",
            LastUpdatedAt = DateTime.UtcNow,
            Version = version
        };
        item.GenerateIntegrityHash();
        return item;
    }

    private static TombstoneCausalReference Reference(SyncVersionStamp version) => new()
    {
        OriginDeviceId = version.OriginDeviceId,
        OriginInstanceId = version.OriginInstanceId,
        UserKeyEpoch = 1,
        MembershipEpoch = 1,
        OriginRevision = Math.Max(1, version.LogicalCounter + 1)
    };

    private static DeletedPasswordData DeletedPassword(Guid id, SyncVersionStamp version)
    {
        var item = new DeletedPasswordData { Id = id, DeletedAt = DateTime.UtcNow, Version = version, CausalReference = Reference(version) };
        item.GenerateIntegrityHash();
        return item;
    }

    private static DeletedPasswordTagData DeletedTag(Guid id, SyncVersionStamp version)
    {
        var item = new DeletedPasswordTagData { Id = id, DeletedAt = DateTime.UtcNow, Version = version, CausalReference = Reference(version) };
        item.GenerateIntegrityHash();
        return item;
    }

    private static DeletedCustomUserColorData DeletedColor(Guid id, SyncVersionStamp version)
    {
        var item = new DeletedCustomUserColorData { Id = id, DeletedAt = DateTime.UtcNow, Version = version, CausalReference = Reference(version) };
        item.GenerateIntegrityHash();
        return item;
    }

    private static UserDeviceData UserDevice(Guid id, string name, SyncVersionStamp version, int loginDay)
    {
        var item = new UserDeviceData
        {
            Id = id,
            Name = name,
            LinkedAt = DateTimeOffset.UnixEpoch,
            LastLoginDate = new DateTime(2026, 1, loginDay, 0, 0, 0, DateTimeKind.Utc),
            PreviousLoginDate = new DateTime(2026, 1, Math.Max(1, loginDay - 1), 0, 0, 0, DateTimeKind.Utc),
            LastUpdatedAt = DateTimeOffset.UnixEpoch.AddDays(loginDay),
            Version = version
        };
        item.GenerateIntegrityHash();
        return item;
    }

    private static DeletedUserDeviceData DeletedDevice(Guid id, SyncVersionStamp version)
    {
        var item = new DeletedUserDeviceData
        {
            Id = id,
            DeletedAt = DateTimeOffset.UtcNow,
            Version = version,
            CausalReference = Reference(version)
        };
        item.GenerateIntegrityHash();
        return item;
    }

    private static UserPasswordsData Passwords(
        SecurePassword? password = null,
        PasswordTag? tag = null,
        CustomUserColor? color = null,
        DeletedPasswordData? deleted = null,
        DeletedPasswordTagData? deletedTag = null,
        DeletedCustomUserColorData? deletedColor = null)
    {
        var data = new UserPasswordsData { PasswordKey = new byte[32] };
        if (password is not null) data.Passwords.Add(password);
        if (tag is not null) data.Tags.Add(tag);
        if (color is not null) data.CustomColors.Add(color);
        if (deleted is not null) data.DeletedPasswords.Add(deleted);
        if (deletedTag is not null) data.DeletedTags.Add(deletedTag);
        if (deletedColor is not null) data.DeletedCustomColors.Add(deletedColor);
        data.GenerateIntegrityHash();
        return data;
    }

    private static UserDevicesData Devices(UserDeviceData? device = null, DeletedUserDeviceData? deleted = null)
    {
        var data = new UserDevicesData();
        if (device is not null) data.Devices.Add(device);
        if (deleted is not null) data.DeletedDevices.Add(deleted);
        data.GenerateIntegrityHash();
        return data;
    }

    internal static GeneralUserData Clone(GeneralUserData source)
    {
        ArgumentNullException.ThrowIfNull(source);

        var data = new GeneralUserData
        {
            Username = source.Username,
            FirstName = source.FirstName,
            LastName = source.LastName,
            Email = source.Email,
            RegistrationDate = source.RegistrationDate,
            RegistrationTimeZoneId = source.RegistrationTimeZoneId,
            RegistrationDeviceType = source.RegistrationDeviceType,
            LastUpdatedAt = source.LastUpdatedAt,
            Version = source.Version
        };
        data.GenerateIntegrityHash();
        return data;
    }

    internal static UserPasswordsData Clone(UserPasswordsData source)
    {
        var data = new UserPasswordsData { PasswordKey = source.PasswordKey.ToArray() };
        foreach (var item in source.Passwords)
        {
            var clone = new SecurePassword
            {
                Id = item.Id,
                Name = item.Name,
                Description = item.Description,
                Color = item.Color,
                Password = item.Password.ToArray(),
                TagIds = item.TagIds.ToList(),
                CreatedAt = item.CreatedAt,
                LastUpdatedAt = item.LastUpdatedAt,
                Version = item.Version
            };
            clone.GenerateIntegrityHash();
            data.Passwords.Add(clone);
        }
        foreach (var item in source.DeletedPasswords)
        {
            var clone = new DeletedPasswordData { Id = item.Id, DeletedAt = item.DeletedAt, Version = item.Version, CausalReference = item.CausalReference };
            clone.GenerateIntegrityHash();
            data.DeletedPasswords.Add(clone);
        }
        foreach (var item in source.Tags)
        {
            var clone = new PasswordTag
            {
                Id = item.Id,
                Name = item.Name,
                Color = item.Color,
                LastUpdatedAt = item.LastUpdatedAt,
                Version = item.Version
            };
            clone.GenerateIntegrityHash();
            data.Tags.Add(clone);
        }
        foreach (var item in source.DeletedTags)
        {
            var clone = new DeletedPasswordTagData { Id = item.Id, DeletedAt = item.DeletedAt, Version = item.Version, CausalReference = item.CausalReference };
            clone.GenerateIntegrityHash();
            data.DeletedTags.Add(clone);
        }
        foreach (var item in source.CustomColors)
        {
            var clone = new CustomUserColor
            {
                Id = item.Id,
                ColorName = item.ColorName,
                ColorCode = item.ColorCode,
                LastUpdatedAt = item.LastUpdatedAt,
                Version = item.Version
            };
            clone.GenerateIntegrityHash();
            data.CustomColors.Add(clone);
        }
        foreach (var item in source.DeletedCustomColors)
        {
            var clone = new DeletedCustomUserColorData { Id = item.Id, DeletedAt = item.DeletedAt, Version = item.Version, CausalReference = item.CausalReference };
            clone.GenerateIntegrityHash();
            data.DeletedCustomColors.Add(clone);
        }
        data.GenerateIntegrityHash();
        return data;
    }

    internal static UserDevicesData Clone(UserDevicesData source)
    {
        var data = new UserDevicesData();
        foreach (var item in source.Devices)
        {
            var clone = new UserDeviceData
            {
                Id = item.Id,
                Name = item.Name,
                LinkedAt = item.LinkedAt,
                LastLoginDate = item.LastLoginDate,
                PreviousLoginDate = item.PreviousLoginDate,
                LastUpdatedAt = item.LastUpdatedAt,
                Version = item.Version
            };
            clone.GenerateIntegrityHash();
            data.Devices.Add(clone);
        }
        foreach (var item in source.DeletedDevices)
        {
            var clone = new DeletedUserDeviceData { Id = item.Id, DeletedAt = item.DeletedAt, Version = item.Version, CausalReference = item.CausalReference };
            clone.GenerateIntegrityHash();
            data.DeletedDevices.Add(clone);
        }
        data.GenerateIntegrityHash();
        return data;
    }


    private static IEnumerable<T[]> Permutations<T>(T[] values)
    {
        for (var first = 0; first < values.Length; first++)
        for (var second = 0; second < values.Length; second++)
        for (var third = 0; third < values.Length; third++)
        {
            if (first == second || first == third || second == third)
                continue;
            yield return [values[first], values[second], values[third]];
        }
    }

}
