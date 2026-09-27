using PasswordManagerLocal.Common.Backend.Abstractions.Services;
using PasswordManagerLocal.Common.Backend.Models;
using PasswordManagerLocal.Common.Backend.Models.Encrypted;
using PasswordManagerLocal.Common.Backend.Security;
using PasswordManagerLocal.Common.Backend.Constants;
using static PasswordManagerLocal.Common.Contracts.Constants.PasswordConstants;
using static PasswordManagerLocal.Common.Backend.Constants.TombstoneConstants;
using static PasswordManagerLocal.Common.Contracts.Validation.DataValidation;

namespace PasswordManagerLocal.Common.Backend.Services;

/// <summary>
/// Validates structural limits and cross-reference invariants before encrypted user data is persisted.
/// </summary>
public sealed class UserDataPersistenceValidator : IUserDataPersistenceValidator
{
    public void EnsureUserDataCanBePersisted(UserData userData, User user)
    {
        if (userData.FormatVersion != SyncConstants.EncryptedUserDataFormatVersion ||
            userData.UId == Guid.Empty || userData.UId != user.UId)
            throw new InvalidOperationException("Refusing to persist invalid user data.");

        if (userData.GeneralUserDataKey.Length == 0 ||
            userData.UserPasswordsDataKey.Length == 0 ||
            userData.UserDevicesDataKey.Length == 0 ||
            userData.GeneralUserDataIntegrityHash.Length != CryptographyConstants.Sha256HashSizeInBytes ||
            userData.UserPasswordsDataIntegrityHash.Length != CryptographyConstants.Sha256HashSizeInBytes ||
            userData.UserDevicesDataIntegrityHash.Length != CryptographyConstants.Sha256HashSizeInBytes)
            throw new InvalidOperationException("Refusing to persist incomplete user data.");
    }

    public void EnsureUserDataBundleCanBePersisted(UserDataBundle bundle, User user)
    {
        EnsureUserDataCanBePersisted(bundle.UserData, user);
        EnsurePasswordDataCanBePersisted(bundle.UserPasswordsData);
        EnsureUserDeviceDataCanBePersisted(bundle.UserDevicesData);
        EnsureCustomColorsCanBePersisted(bundle.UserPasswordsData);
        EnsurePasswordTagsCanBePersisted(bundle.UserPasswordsData);
        EnsurePasswordTagReferencesCanBePersisted(bundle.UserPasswordsData);
    }

    private void EnsurePasswordDataCanBePersisted(UserPasswordsData passwordsData)
    {
        if (passwordsData.PasswordKey.Length == 0)
            throw new InvalidOperationException("Refusing to persist incomplete user data.");

        if (passwordsData.Passwords.Count > MaxNumberOfPasswords)
            throw new InvalidOperationException("Refusing to persist too many passwords.");

        if (passwordsData.CustomColors.Count > MaxNumberOfCustomUserColors)
            throw new InvalidOperationException("Refusing to persist too many custom colors.");

        if (passwordsData.Tags.Count > MaxNumberOfPasswordTags)
            throw new InvalidOperationException("Refusing to persist too many password tags.");

        if (passwordsData.DeletedPasswords.Count > MaxRetainedUserDataTombstonesPerList ||
            passwordsData.DeletedCustomColors.Count > MaxRetainedUserDataTombstonesPerList ||
            passwordsData.DeletedTags.Count > MaxRetainedUserDataTombstonesPerList)
            throw new InvalidOperationException("Refusing to persist too many user data tombstones.");
    }

    private void EnsureUserDeviceDataCanBePersisted(UserDevicesData? userDevicesData)
    {
        if (userDevicesData is null)
            throw new InvalidOperationException("Refusing to persist incomplete user data.");

        if (userDevicesData.DeletedDevices.Count > MaxRetainedUserDataTombstonesPerList)
            throw new InvalidOperationException("Refusing to persist too many user data tombstones.");

        if (userDevicesData.Devices.Any(device =>
                device.Id == Guid.Empty ||
                device.LinkedAt == default ||
                !IsValidUserDeviceName(device.Name)))
            throw new InvalidOperationException("Refusing to persist invalid device data.");

        if (HasDuplicates(userDevicesData.Devices, device => device.Id))
            throw new InvalidOperationException("Refusing to persist duplicate device data.");

        // Concurrent presentation-name edits may legitimately converge to the same name.
        // The stored logical items remain untouched; device responses resolve duplicates as a
        // deterministic derived view ordered by item version and device ID.
    }

    private void EnsureCustomColorsCanBePersisted(UserPasswordsData passwordsData)
    {
        if (passwordsData.CustomColors.Any(color =>
                color.Id == Guid.Empty ||
                !IsValidARGBColor(color.ColorCode) ||
                !IsValidCustomUserColorName(color.ColorName)))
            throw new InvalidOperationException("Refusing to persist invalid custom color data.");

        if (HasDuplicates(passwordsData.CustomColors, color => color.Id))
            throw new InvalidOperationException("Refusing to persist duplicate custom color data.");

        // Concurrent creations on different devices may legitimately produce the same
        // presentation name or color code under distinct item IDs. Local mutation services
        // still reject such duplicates; merged authenticated items must remain persistable.
    }

    private void EnsurePasswordTagsCanBePersisted(UserPasswordsData passwordsData)
    {
        if (passwordsData.Tags.Any(tag =>
                tag.Id == Guid.Empty ||
                !IsValidPasswordTagName(tag.Name) ||
                !IsValidARGBColor(tag.Color)))
            throw new InvalidOperationException("Refusing to persist invalid password tag data.");

        if (HasDuplicates(passwordsData.Tags, tag => tag.Id))
            throw new InvalidOperationException("Refusing to persist duplicate password tag data.");

        // Concurrent same-name tags are distinct authenticated items. Responses are
        // deterministically ordered by name and ID, while local mutations still enforce
        // the normal uniqueness rule for newly authored changes.
    }

    private void EnsurePasswordTagReferencesCanBePersisted(UserPasswordsData passwordsData)
    {
        // References to tags that lost to a deletion are retained as deterministic derived data.
        // Read models filter them against the live tag set; removing them during merge would
        // otherwise require a device-local mutation version and break convergence.
        var hasInvalidReferences = passwordsData.Passwords.Any(password =>
            password.TagIds.Any(tagId => tagId == Guid.Empty) ||
            password.TagIds.Distinct().Count() != password.TagIds.Count);

        if (hasInvalidReferences)
            throw new InvalidOperationException("Refusing to persist invalid password tag references.");
    }

    private bool HasDuplicates<TItem, TKey>(
        IEnumerable<TItem> items,
        Func<TItem, TKey> keySelector,
        IEqualityComparer<TKey>? comparer = null) =>
        items.GroupBy(keySelector, comparer).Any(group => group.Count() != 1);
}
