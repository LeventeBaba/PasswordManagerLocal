using Microsoft.VisualStudio.TestTools.UnitTesting;
using PasswordManagerLocal.Common.Backend.Constants;
using PasswordManagerLocal.Common.Backend.Exceptions;
using PasswordManagerLocal.Common.Backend.Models;
using PasswordManagerLocal.Common.Backend.Models.Encrypted;
using PasswordManagerLocal.Common.Backend.Security;

using MSTestAssert = Microsoft.VisualStudio.TestTools.UnitTesting.Assert;

namespace PasswordManagerLocal.Common.Tests.Backend.Models;

[TestClass]
public sealed class IntegrityProtectionTests
{
    [TestMethod]
    [TestCategory("Backend")]
    [TestCategory("Security")]
    public void SecurePassword_TamperingIsDetected()
    {
        using var password = CreateSecurePassword();
        password.GenerateIntegrityHash();

        MSTestAssert.HasCount(CryptographyConstants.Sha256HashSizeInBytes, password.IntegrityHash);
        MSTestAssert.IsTrue(password.IsIntegrityValid());

        password.Password[0] ^= 0x01;

        MSTestAssert.IsFalse(password.IsIntegrityValid());
        ExpectThrows<InvalidDataIntegrityException>(password.VerifyIntegrity);
    }

    [TestMethod]
    [TestCategory("Backend")]
    [TestCategory("Security")]
    public void Device_GenerateIntegrityHashBindsSigningKeyAndDerivedHash()
    {
        var device = new Device
        {
            Id = Guid.Parse("D4DE4846-C997-45AF-A26B-9B3C345EAE5A"),
            PublicKey = Enumerable.Repeat((byte)1, 32).ToArray(),
            SignPublicKey = Enumerable.Repeat((byte)2, 32).ToArray(),
            TlsCertFingerprint = "AABBCCDD",
            DeviceType = DeviceType.WindowsPc,
            LastKnownHash = [3, 4, 5],
            LastSync = new DateTime(2026, 1, 2, 3, 4, 5, DateTimeKind.Utc),
            LastSeen = new DateTime(2026, 1, 2, 4, 4, 5, DateTimeKind.Utc),
            IsTrusted = true,
            LastModifiedAt = new DateTimeOffset(2026, 1, 2, 5, 4, 5, TimeSpan.Zero)
        };

        device.GenerateIntegrityHash();

        MSTestAssert.HasCount(CryptographyConstants.Sha256HashSizeInBytes, device.SignPublicKeyHash);
        MSTestAssert.HasCount(CryptographyConstants.Sha256HashSizeInBytes, device.IntegrityHash);
        MSTestAssert.IsTrue(device.IsIntegrityValid());

        device.SignPublicKey[0] ^= 0x01;

        MSTestAssert.IsFalse(device.IsIntegrityValid());
        ExpectThrows<InvalidDataIntegrityException>(device.VerifyIntegrity);
    }

    [TestMethod]
    [TestCategory("Backend")]
    [TestCategory("Security")]
    public void UserDevice_GenerateIntegrityHashCreatesAndBindsDeterministicModelId()
    {
        var link = new UserDevice
        {
            UserId = Guid.Parse("91A04F56-B972-4BFA-B020-6A2135988C83"),
            DeviceId = Guid.Parse("F530B2D3-AEF1-42F0-A623-F308CC3E258E"),
            IsSyncOn = true,
            LastModifiedAt = new DateTimeOffset(2026, 2, 3, 4, 5, 6, TimeSpan.Zero)
        };

        link.GenerateIntegrityHash();
        var generatedModelId = link.ModelId;

        MSTestAssert.AreNotEqual(Guid.Empty, generatedModelId);
        MSTestAssert.IsTrue(link.IsIntegrityValid());

        link.UserId = Guid.NewGuid();

        MSTestAssert.IsFalse(link.IsIntegrityValid());
        ExpectThrows<InvalidDataIntegrityException>(link.VerifyIntegrity);
    }

    [TestMethod]
    [TestCategory("Backend")]
    [TestCategory("Security")]
    public void UserDeviceData_PreviousLoginDateIsIntegrityProtected()
    {
        using var device = CreateUserDeviceData();
        device.GenerateIntegrityHash();
        MSTestAssert.IsTrue(device.IsIntegrityValid());

        device.PreviousLoginDate = device.PreviousLoginDate!.Value.AddMinutes(-1);

        MSTestAssert.IsFalse(device.IsIntegrityValid());
        ExpectThrows<InvalidDataIntegrityException>(device.VerifyIntegrity);
    }

    [TestMethod]
    [TestCategory("Backend")]
    [TestCategory("Security")]
    public void UserDeviceData_LegacyHashWithoutPreviousLoginDateIsRejected()
    {
        using var device = CreateUserDeviceData();
        device.PreviousLoginDate = null;
        device.IntegrityHash = Hashing.SHA256Hash(hash =>
        {
            hash.Write(device.Id);
            hash.WriteString(device.Name);
            hash.Write(device.LinkedAt);
            hash.Write(device.LastLoginDate);
            hash.Write(device.LastUpdatedAt);
            device.Version.WriteTo(hash);
        });

        MSTestAssert.IsFalse(device.IsIntegrityValid());
        ExpectThrows<InvalidDataIntegrityException>(device.VerifyIntegrity);
    }

    [TestMethod]
    [TestCategory("Backend")]
    [TestCategory("Security")]
    public void GeneralUserData_RegistrationMetadataIsIntegrityProtected()
    {
        using var timeZoneTampered = CreateGeneralUserData();
        timeZoneTampered.GenerateIntegrityHash();
        MSTestAssert.IsTrue(timeZoneTampered.IsIntegrityValid());

        timeZoneTampered.RegistrationTimeZoneId = "America/New_York";
        MSTestAssert.IsFalse(timeZoneTampered.IsIntegrityValid());

        using var deviceTypeTampered = CreateGeneralUserData();
        deviceTypeTampered.GenerateIntegrityHash();
        deviceTypeTampered.RegistrationDeviceType = DeviceType.AndroidMobile;
        MSTestAssert.IsFalse(deviceTypeTampered.IsIntegrityValid());
    }

    [TestMethod]
    [TestCategory("Backend")]
    [TestCategory("Security")]
    public void GeneralUserData_LegacyHashWithoutRegistrationMetadataIsRejected()
    {
        using var data = CreateGeneralUserData();
        data.IntegrityHash = Hashing.SHA256Hash(hash =>
        {
            hash.WriteString(data.Username);
            hash.WriteString(data.FirstName);
            hash.WriteString(data.LastName);
            hash.WriteString(data.Email);
            hash.Write(data.RegistrationDate);
            hash.Write(data.LastUpdatedAt);
            data.Version.WriteTo(hash);
        });

        MSTestAssert.IsFalse(data.IsIntegrityValid());
        ExpectThrows<InvalidDataIntegrityException>(data.VerifyIntegrity);
    }

    [TestMethod]
    [TestCategory("Backend")]
    [TestCategory("Security")]
    public void LocalDeviceIdentity_PrivateKeyTamperingIsDetected()
    {
        var identity = new LocalDeviceIdentity
        {
            Id = Guid.NewGuid(),
            AgreementPrivateKeyBlob = Enumerable.Repeat((byte)1, 48).ToArray(),
            SignPrivateKeyBlob = Enumerable.Repeat((byte)2, 48).ToArray(),
            PFXCertificate = Enumerable.Repeat((byte)3, 128).ToArray(),
            DeviceType = DeviceType.AndroidMobile,
            IsSyncOn = true,
            CreatedAt = new DateTimeOffset(2026, 3, 4, 5, 6, 7, TimeSpan.Zero)
        };
        identity.GenerateIntegrityHash();

        identity.AgreementPrivateKeyBlob[10] ^= 0x40;

        MSTestAssert.IsFalse(identity.IsIntegrityValid());
        ExpectThrows<InvalidDataIntegrityException>(identity.VerifyIntegrity);
    }

    [TestMethod]
    [TestCategory("Backend")]
    [TestCategory("Security")]
    public void UserData_ChildIntegrityHashTamperingIsDetectedByParent()
    {
        using var data = CreateUserData();
        data.GenerateIntegrityHash();
        MSTestAssert.IsTrue(data.IsIntegrityValid());

        data.UserPasswordsDataIntegrityHash[0] ^= 0x20;

        MSTestAssert.IsFalse(data.IsIntegrityValid());
        ExpectThrows<InvalidDataIntegrityException>(data.VerifyIntegrity);
    }


    [TestMethod]
    [TestCategory("Backend")]
    [TestCategory("Security")]
    public void DeletedPassword_CausalReferenceTamperingIsDetected()
    {
        var deviceId = Guid.NewGuid();
        var instanceId = Guid.NewGuid();
        using var tombstone = new DeletedPasswordData
        {
            Id = Guid.NewGuid(),
            Version = new SyncVersionStamp
            {
                PhysicalTimeUnixMilliseconds = 1_700_000_000_000,
                LogicalCounter = 1,
                OriginDeviceId = deviceId,
                OriginInstanceId = instanceId
            },
            CausalReference = new TombstoneCausalReference
            {
                OriginDeviceId = deviceId,
                OriginInstanceId = instanceId,
                UserKeyEpoch = 1,
                MembershipEpoch = 1,
                OriginRevision = 4
            }
        };
        tombstone.GenerateIntegrityHash();

        tombstone.CausalReference = tombstone.CausalReference with { OriginRevision = 5 };

        MSTestAssert.IsFalse(tombstone.IsIntegrityValid());
        ExpectThrows<InvalidDataIntegrityException>(tombstone.VerifyIntegrity);
    }

    [TestMethod]
    [TestCategory("Backend")]
    [TestCategory("Security")]
    public void SecurePassword_DisposeZeroesCallerObservableSecretBuffers()
    {
        var password = CreateSecurePassword();
        password.GenerateIntegrityHash();
        var passwordBuffer = password.Password;
        var integrityBuffer = password.IntegrityHash;

        password.Dispose();

        MSTestAssert.IsTrue(passwordBuffer.All(value => value == 0));
        MSTestAssert.IsTrue(integrityBuffer.All(value => value == 0));
        MSTestAssert.AreEqual(Guid.Empty, password.Id);
        MSTestAssert.AreEqual(string.Empty, password.Name);
    }

    [TestMethod]
    [TestCategory("Backend")]
    [TestCategory("Security")]
    public void SecurePasswords_DisposeZeroesKeyAndContainedPasswords()
    {
        var child = CreateSecurePassword();
        child.GenerateIntegrityHash();
        var childSecret = child.Password;
        var key = Enumerable.Repeat((byte)9, 32).ToArray();
        var passwords = new SecurePasswords
        {
            PasswordKey = key,
            Passwords = [child]
        };
        passwords.GenerateIntegrityHash();

        passwords.Dispose();

        MSTestAssert.IsTrue(key.All(value => value == 0));
        MSTestAssert.IsTrue(childSecret.All(value => value == 0));
        MSTestAssert.IsEmpty(passwords.Passwords);
    }


    [TestMethod]
    [TestCategory("Backend")]
    [TestCategory("Security")]
    public void UserPasswordsData_GenerateIntegrityHashCreatesCategoryHashes()
    {
        using var data = CreateUserPasswordsDataWithCategories();

        data.GenerateIntegrityHash();

        MSTestAssert.HasCount(CryptographyConstants.Sha256HashSizeInBytes, data.PasswordsIntegrityHash);
        MSTestAssert.HasCount(CryptographyConstants.Sha256HashSizeInBytes, data.CustomColorsIntegrityHash);
        MSTestAssert.HasCount(CryptographyConstants.Sha256HashSizeInBytes, data.PasswordTagsIntegrityHash);
        MSTestAssert.HasCount(CryptographyConstants.Sha256HashSizeInBytes, data.IntegrityHash);
        MSTestAssert.IsTrue(data.IsIntegrityValid());
    }

    [TestMethod]
    [TestCategory("Backend")]
    [TestCategory("Security")]
    public void UserPasswordsData_IncrementalCategoryHashKeepsUnchangedCategoriesStable()
    {
        using var data = CreateUserPasswordsDataWithCategories();
        data.GenerateIntegrityHash();
        var originalPasswordsHash = data.PasswordsIntegrityHash.ToArray();
        var originalCustomColorsHash = data.CustomColorsIntegrityHash.ToArray();
        var originalPasswordTagsHash = data.PasswordTagsIntegrityHash.ToArray();
        var originalRootHash = data.IntegrityHash.ToArray();

        data.Tags[0].Name = "Urgent";
        data.Tags[0].GenerateIntegrityHash();
        data.GeneratePasswordTagsIntegrityHash();

        CollectionAssert.AreEqual(originalPasswordsHash, data.PasswordsIntegrityHash);
        CollectionAssert.AreEqual(originalCustomColorsHash, data.CustomColorsIntegrityHash);
        MSTestAssert.IsFalse(originalPasswordTagsHash.SequenceEqual(data.PasswordTagsIntegrityHash));
        MSTestAssert.IsFalse(originalRootHash.SequenceEqual(data.IntegrityHash));
        MSTestAssert.IsTrue(data.IsIntegrityValid());
    }

    [TestMethod]
    [TestCategory("Backend")]
    [TestCategory("Security")]
    public void UserPasswordsData_CategoryHashTamperingIsDetected()
    {
        using var data = CreateUserPasswordsDataWithCategories();
        data.GenerateIntegrityHash();

        data.PasswordTagsIntegrityHash[0] ^= 0x10;

        MSTestAssert.IsFalse(data.IsIntegrityValid());
        ExpectThrows<InvalidDataIntegrityException>(data.VerifyIntegrity);
    }

    [TestMethod]
    [TestCategory("Backend")]
    [TestCategory("Security")]
    public void UserPasswordsData_ChildHashTamperingIsDetectedByCategoryHash()
    {
        using var data = CreateUserPasswordsDataWithCategories();
        data.GenerateIntegrityHash();

        data.CustomColors[0].IntegrityHash[0] ^= 0x10;

        MSTestAssert.IsFalse(data.IsIntegrityValid());
        ExpectThrows<InvalidDataIntegrityException>(data.VerifyIntegrity);
    }

    private static SecurePassword CreateSecurePassword() =>
        new()
        {
            Id = Guid.Parse("5E955FCD-D3BA-47C9-AB2E-13D0A6389624"),
            Name = "Email",
            Description = "Primary account",
            Color = PasswordConstants.DefaultPasswordColor,
            Password = [10, 20, 30, 40, 50],
            CreatedAt = new DateTime(2026, 1, 1, 1, 2, 3, DateTimeKind.Utc),
            LastUpdatedAt = new DateTime(2026, 1, 1, 2, 3, 4, DateTimeKind.Utc)
        };


    private static UserPasswordsData CreateUserPasswordsDataWithCategories()
    {
        var password = CreateSecurePassword();
        password.GenerateIntegrityHash();

        var deletedPassword = new DeletedPasswordData
        {
            Id = Guid.Parse("A4B3E9A1-622B-4EF7-9C35-6C0D5C274D30"),
            DeletedAt = new DateTime(2026, 1, 1, 3, 4, 5, DateTimeKind.Utc)
        };
        deletedPassword.GenerateIntegrityHash();

        var customColor = new CustomUserColor
        {
            Id = Guid.Parse("D38904A9-1900-4C6E-83A0-E56B97D4B71B"),
            ColorName = "Ocean",
            ColorCode = "#FF006699",
            LastUpdatedAt = new DateTime(2026, 1, 1, 4, 5, 6, DateTimeKind.Utc)
        };
        customColor.GenerateIntegrityHash();

        var deletedCustomColor = new DeletedCustomUserColorData
        {
            Id = Guid.Parse("85C6B3A8-A1E1-4940-A393-DB20BD7F91D0"),
            DeletedAt = new DateTime(2026, 1, 1, 5, 6, 7, DateTimeKind.Utc)
        };
        deletedCustomColor.GenerateIntegrityHash();

        var tag = new PasswordTag
        {
            Id = Guid.Parse("809C286C-519D-4F5B-9162-6419EC5CA79E"),
            Name = "Work",
            Color = PasswordConstants.DefaultPasswordColor,
            LastUpdatedAt = new DateTime(2026, 1, 1, 6, 7, 8, DateTimeKind.Utc)
        };
        tag.GenerateIntegrityHash();

        var deletedTag = new DeletedPasswordTagData
        {
            Id = Guid.Parse("E900AF42-4BB5-42CB-BE0E-10B105E7BB37"),
            DeletedAt = new DateTime(2026, 1, 1, 7, 8, 9, DateTimeKind.Utc)
        };
        deletedTag.GenerateIntegrityHash();

        return new UserPasswordsData
        {
            PasswordKey = Enumerable.Repeat((byte)9, 32).ToArray(),
            Passwords = [password],
            DeletedPasswords = [deletedPassword],
            CustomColors = [customColor],
            DeletedCustomColors = [deletedCustomColor],
            Tags = [tag],
            DeletedTags = [deletedTag]
        };
    }

    private static GeneralUserData CreateGeneralUserData() => new()
    {
        Username = "integrity-user",
        FirstName = "Integrity",
        LastName = "User",
        Email = "integrity@example.test",
        RegistrationDate = new DateTime(2026, 1, 2, 3, 4, 5, DateTimeKind.Utc),
        RegistrationTimeZoneId = "Europe/Budapest",
        RegistrationDeviceType = DeviceType.WindowsPc,
        LastUpdatedAt = new DateTime(2026, 1, 2, 4, 5, 6, DateTimeKind.Utc),
        Version = new SyncVersionStamp
        {
            PhysicalTimeUnixMilliseconds = 1_767_326_400_000,
            LogicalCounter = 1,
            OriginDeviceId = Guid.Parse("A8541601-352A-4671-9599-E56B4B9A6228"),
            OriginInstanceId = Guid.Parse("146F1A7C-6A81-469A-90E4-AC045BB6A349")
        }
    };

    private static UserDeviceData CreateUserDeviceData() => new()
    {
        Id = Guid.Parse("820A69AA-3BC7-42EA-A580-CB4689A84804"),
        Name = "Integrity device",
        LinkedAt = new DateTimeOffset(2026, 1, 1, 2, 3, 4, TimeSpan.Zero),
        LastLoginDate = new DateTime(2026, 2, 2, 3, 4, 5, DateTimeKind.Utc),
        PreviousLoginDate = new DateTime(2026, 2, 1, 3, 4, 5, DateTimeKind.Utc),
        LastUpdatedAt = new DateTimeOffset(2026, 2, 2, 3, 4, 5, TimeSpan.Zero),
        Version = new SyncVersionStamp
        {
            PhysicalTimeUnixMilliseconds = 1_770_000_000_000,
            LogicalCounter = 1,
            OriginDeviceId = Guid.Parse("9C2E0B89-0C41-43E0-B568-343E8237B058"),
            OriginInstanceId = Guid.Parse("FA65C9AC-7043-4D5F-BBF0-1F37580B139F")
        }
    };

    private static UserData CreateUserData()
    {
        return new UserData
        {
            UId = Guid.Parse("BD07BBD9-2ECB-4AC7-BD79-0E395BCF0414"),
            GeneralUserDataKey = Enumerable.Repeat((byte)1, 32).ToArray(),
            GeneralUserDataIntegrityHash = Enumerable.Repeat((byte)2, 32).ToArray(),
            UserPasswordsDataKey = Enumerable.Repeat((byte)3, 32).ToArray(),
            UserPasswordsDataIntegrityHash = Enumerable.Repeat((byte)4, 32).ToArray(),
            UserDevicesDataKey = Enumerable.Repeat((byte)5, 32).ToArray(),
            UserDevicesDataIntegrityHash = Enumerable.Repeat((byte)6, 32).ToArray()
        };
    }

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
