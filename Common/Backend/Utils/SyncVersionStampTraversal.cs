using PasswordManagerLocal.Common.Backend.Models.Encrypted;

using PasswordManagerLocal.Common.Backend.Sync.Tombstones;

namespace PasswordManagerLocal.Common.Backend.Utils;

public static class SyncVersionStampTraversal
{
    public static IEnumerable<SyncVersionStamp> Enumerate(UserDataBundle bundle)
    {
        yield return bundle.GeneralUserData.Version;
        foreach (var stamp in Enumerate(bundle.UserPasswordsData))
            yield return stamp;
        foreach (var stamp in Enumerate(bundle.UserDevicesData))
            yield return stamp;
    }

    public static IEnumerable<SyncVersionStamp> Enumerate(UserPasswordsData data)
    {
        foreach (var item in data.Passwords) yield return item.Version;
        foreach (var item in data.DeletedPasswords) yield return item.Version;
        foreach (var item in data.CustomColors) yield return item.Version;
        foreach (var item in data.DeletedCustomColors) yield return item.Version;
        foreach (var item in data.Tags) yield return item.Version;
        foreach (var item in data.DeletedTags) yield return item.Version;
    }

    public static IEnumerable<SyncVersionStamp> Enumerate(UserDevicesData data)
    {
        foreach (var item in data.Devices)
            yield return item.Version;
        foreach (var item in data.DeletedDevices) yield return item.Version;
    }

    public static void Validate(GeneralUserData data) => SyncVersionStampComparer.Validate(data.Version);

    public static void Validate(UserPasswordsData data)
    {
        foreach (var stamp in Enumerate(data))
            SyncVersionStampComparer.Validate(stamp);
        TombstoneCausalReferenceUtil.Validate(data);
    }

    public static void Validate(UserDevicesData data)
    {
        foreach (var stamp in Enumerate(data))
            SyncVersionStampComparer.Validate(stamp);
        TombstoneCausalReferenceUtil.Validate(data);
    }
}
