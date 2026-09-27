using PasswordManagerLocal.Common.Backend.Models.Encrypted;

using PasswordManagerLocal.Common.Backend.Utils;

namespace PasswordManagerLocal.Common.Backend.Sync.Tombstones;

public static class TombstoneCleanupUtil
{

    public static void AddOrUpdateDeletedPassword(UserPasswordsData passwords, Guid passwordId, DateTime deletedAt, SyncVersionStamp version)
    {
        deletedAt = UtcDateTimeUtil.ToUtc(deletedAt);

        var tombstone = passwords.DeletedPasswords.FirstOrDefault(deleted => deleted.Id == passwordId);
        if (tombstone is null)
        {
            tombstone = new DeletedPasswordData { Id = passwordId };
            passwords.DeletedPasswords.Add(tombstone);
        }

        SyncVersionStampComparer.Validate(version);
        tombstone.DeletedAt = deletedAt;
        tombstone.Version = version;
        tombstone.CausalReference = new();
        tombstone.GenerateIntegrityHash();
    }


    public static void AddOrUpdateDeletedCustomUserColor(UserPasswordsData passwords, Guid customUserColorId, DateTime deletedAt, SyncVersionStamp version)
    {
        deletedAt = UtcDateTimeUtil.ToUtc(deletedAt);

        var tombstone = passwords.DeletedCustomColors.FirstOrDefault(deleted => deleted.Id == customUserColorId);
        if (tombstone is null)
        {
            tombstone = new DeletedCustomUserColorData { Id = customUserColorId };
            passwords.DeletedCustomColors.Add(tombstone);
        }

        SyncVersionStampComparer.Validate(version);
        tombstone.DeletedAt = deletedAt;
        tombstone.Version = version;
        tombstone.CausalReference = new();
        tombstone.GenerateIntegrityHash();
    }


    public static void AddOrUpdateDeletedPasswordTag(UserPasswordsData passwords, Guid passwordTagId, DateTime deletedAt, SyncVersionStamp version)
    {
        deletedAt = UtcDateTimeUtil.ToUtc(deletedAt);

        var tombstone = passwords.DeletedTags.FirstOrDefault(deleted => deleted.Id == passwordTagId);
        if (tombstone is null)
        {
            tombstone = new DeletedPasswordTagData { Id = passwordTagId };
            passwords.DeletedTags.Add(tombstone);
        }

        SyncVersionStampComparer.Validate(version);
        tombstone.DeletedAt = deletedAt;
        tombstone.Version = version;
        tombstone.CausalReference = new();
        tombstone.GenerateIntegrityHash();
    }


    public static void AddOrUpdateDeletedUserDevice(UserDevicesData devices, Guid deviceId, DateTimeOffset deletedAt, SyncVersionStamp version)
    {
        deletedAt = UtcDateTimeUtil.ToUtc(deletedAt);

        var tombstone = devices.DeletedDevices.FirstOrDefault(deleted => deleted.Id == deviceId);
        if (tombstone is null)
        {
            tombstone = new DeletedUserDeviceData { Id = deviceId };
            devices.DeletedDevices.Add(tombstone);
        }

        SyncVersionStampComparer.Validate(version);
        tombstone.DeletedAt = deletedAt;
        tombstone.Version = version;
        tombstone.CausalReference = new();
        tombstone.GenerateIntegrityHash();
    }

}
