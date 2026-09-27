using PasswordManagerLocal.Common.Backend.Models.Encrypted;

using PasswordManagerLocal.Common.Backend.Utils;

namespace PasswordManagerLocal.Common.Backend.Sync.Tombstones;

/// <summary>
/// Assigns the first covering local snapshot identity to newly authored deletion mutations.
/// Existing authenticated references are immutable and are only validated here.
/// </summary>
public static class TombstoneCausalReferenceUtil
{
    public static UserDataBlobKind AssignAndValidate(
        UserDataBundle bundle,
        Guid localDeviceId,
        Guid localOriginInstanceId,
        long userKeyEpoch,
        long membershipEpoch,
        long nextOriginRevision,
        UserDataBlobKind modifiedBlobs)
    {
        if (localDeviceId == Guid.Empty || localOriginInstanceId == Guid.Empty ||
            userKeyEpoch <= 0 || membershipEpoch <= 0 || nextOriginRevision <= 0)
        {
            throw new InvalidOperationException("Cannot assign tombstone causal references from invalid local snapshot state.");
        }

        var changed = UserDataBlobKind.None;
        if (modifiedBlobs.HasFlag(UserDataBlobKind.Passwords))
        {
            changed |= AssignAndValidate(
                bundle.UserPasswordsData.DeletedPasswords,
                localDeviceId,
                localOriginInstanceId,
                userKeyEpoch,
                membershipEpoch,
                nextOriginRevision)
                ? UserDataBlobKind.Passwords
                : UserDataBlobKind.None;
            changed |= AssignAndValidate(
                bundle.UserPasswordsData.DeletedCustomColors,
                localDeviceId,
                localOriginInstanceId,
                userKeyEpoch,
                membershipEpoch,
                nextOriginRevision)
                ? UserDataBlobKind.Passwords
                : UserDataBlobKind.None;
            changed |= AssignAndValidate(
                bundle.UserPasswordsData.DeletedTags,
                localDeviceId,
                localOriginInstanceId,
                userKeyEpoch,
                membershipEpoch,
                nextOriginRevision)
                ? UserDataBlobKind.Passwords
                : UserDataBlobKind.None;
        }

        if (modifiedBlobs.HasFlag(UserDataBlobKind.Devices) && AssignAndValidate(
                bundle.UserDevicesData.DeletedDevices,
                localDeviceId,
                localOriginInstanceId,
                userKeyEpoch,
                membershipEpoch,
                nextOriginRevision))
        {
            changed |= UserDataBlobKind.Devices;
        }

        return changed;
    }

    public static void Validate(UserPasswordsData data)
    {
        foreach (var item in data.DeletedPasswords)
            Validate(item.Version, item.CausalReference);
        foreach (var item in data.DeletedCustomColors)
            Validate(item.Version, item.CausalReference);
        foreach (var item in data.DeletedTags)
            Validate(item.Version, item.CausalReference);
    }

    public static void Validate(UserDevicesData data)
    {
        foreach (var item in data.DeletedDevices)
            Validate(item.Version, item.CausalReference);
    }

    public static void Validate(SyncVersionStamp version, TombstoneCausalReference reference)
    {
        SyncVersionStampComparer.Validate(version);
        if (reference is null || !reference.IsValid)
            throw new InvalidDataException("A deletion tombstone is missing its authenticated causal snapshot reference.");
        if (reference.OriginDeviceId != version.OriginDeviceId ||
            reference.OriginInstanceId != version.OriginInstanceId)
        {
            throw new InvalidDataException("A deletion tombstone causal reference does not match its immutable item-version origin.");
        }
    }

    private static bool AssignAndValidate(
        IEnumerable<DeletedPasswordData> tombstones,
        Guid localDeviceId,
        Guid localOriginInstanceId,
        long userKeyEpoch,
        long membershipEpoch,
        long nextOriginRevision)
    {
        var changed = false;
        foreach (var tombstone in tombstones)
        {
            changed |= AssignIfMissing(tombstone.Version, tombstone.CausalReference, value => tombstone.CausalReference = value,
                localDeviceId, localOriginInstanceId, userKeyEpoch, membershipEpoch, nextOriginRevision);
            tombstone.GenerateIntegrityHash();
        }
        return changed;
    }

    private static bool AssignAndValidate(
        IEnumerable<DeletedCustomUserColorData> tombstones,
        Guid localDeviceId,
        Guid localOriginInstanceId,
        long userKeyEpoch,
        long membershipEpoch,
        long nextOriginRevision)
    {
        var changed = false;
        foreach (var tombstone in tombstones)
        {
            changed |= AssignIfMissing(tombstone.Version, tombstone.CausalReference, value => tombstone.CausalReference = value,
                localDeviceId, localOriginInstanceId, userKeyEpoch, membershipEpoch, nextOriginRevision);
            tombstone.GenerateIntegrityHash();
        }
        return changed;
    }

    private static bool AssignAndValidate(
        IEnumerable<DeletedPasswordTagData> tombstones,
        Guid localDeviceId,
        Guid localOriginInstanceId,
        long userKeyEpoch,
        long membershipEpoch,
        long nextOriginRevision)
    {
        var changed = false;
        foreach (var tombstone in tombstones)
        {
            changed |= AssignIfMissing(tombstone.Version, tombstone.CausalReference, value => tombstone.CausalReference = value,
                localDeviceId, localOriginInstanceId, userKeyEpoch, membershipEpoch, nextOriginRevision);
            tombstone.GenerateIntegrityHash();
        }
        return changed;
    }

    private static bool AssignAndValidate(
        IEnumerable<DeletedUserDeviceData> tombstones,
        Guid localDeviceId,
        Guid localOriginInstanceId,
        long userKeyEpoch,
        long membershipEpoch,
        long nextOriginRevision)
    {
        var changed = false;
        foreach (var tombstone in tombstones)
        {
            changed |= AssignIfMissing(tombstone.Version, tombstone.CausalReference, value => tombstone.CausalReference = value,
                localDeviceId, localOriginInstanceId, userKeyEpoch, membershipEpoch, nextOriginRevision);
            tombstone.GenerateIntegrityHash();
        }
        return changed;
    }

    private static bool AssignIfMissing(
        SyncVersionStamp version,
        TombstoneCausalReference reference,
        Action<TombstoneCausalReference> assign,
        Guid localDeviceId,
        Guid localOriginInstanceId,
        long userKeyEpoch,
        long membershipEpoch,
        long nextOriginRevision)
    {
        SyncVersionStampComparer.Validate(version);
        if (reference is not null && reference.IsValid)
        {
            Validate(version, reference);
            return false;
        }

        if (version.OriginDeviceId != localDeviceId || version.OriginInstanceId != localOriginInstanceId)
            throw new InvalidDataException("An unanchored remote deletion tombstone cannot be persisted.");

        assign(new TombstoneCausalReference
        {
            OriginDeviceId = localDeviceId,
            OriginInstanceId = localOriginInstanceId,
            UserKeyEpoch = userKeyEpoch,
            MembershipEpoch = membershipEpoch,
            OriginRevision = nextOriginRevision
        });
        return true;
    }
}
