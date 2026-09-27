using PasswordManagerLocal.Common.Backend.Constants;
using PasswordManagerLocal.Common.Backend.Abstractions.Services;
using PasswordManagerLocal.Common.Backend.Exceptions;
using PasswordManagerLocal.Common.Backend.Models.Encrypted;
using PasswordManagerLocal.Common.Backend.Security;
using System.Security.Cryptography;
using PasswordManagerLocal.Common.Backend.Utils;

namespace PasswordManagerLocal.Common.Backend.Services;

public sealed class UserDataBundleIntegrityService : IUserDataBundleIntegrityService
{
    public void VerifyUserData(UserData userData) =>
        userData.VerifyIntegrity();


    public void VerifyGeneralUserData(GeneralUserData generalUserData)
    {
        SyncVersionStampTraversal.Validate(generalUserData);
        generalUserData.VerifyIntegrity();
    }


    public void VerifyUserPasswordsData(UserPasswordsData userPasswordsData)
    {
        SyncVersionStampTraversal.Validate(userPasswordsData);
        userPasswordsData.VerifyIntegrity();
        VerifyPasswordChildren(userPasswordsData);
    }


    public void VerifyUserDevicesData(UserDevicesData userDevicesData)
    {
        SyncVersionStampTraversal.Validate(userDevicesData);
        userDevicesData.VerifyIntegrity();
        VerifyDeviceChildren(userDevicesData);
    }


    public void VerifyBundleLinks(UserDataBundle bundle)
    {
        VerifyStoredChildHash(
            bundle.UserData.GeneralUserDataIntegrityHash,
            bundle.GeneralUserData.IntegrityHash,
            typeof(GeneralUserData));
        VerifyStoredChildHash(
            bundle.UserData.UserPasswordsDataIntegrityHash,
            bundle.UserPasswordsData.IntegrityHash,
            typeof(UserPasswordsData));
        VerifyStoredChildHash(
            bundle.UserData.UserDevicesDataIntegrityHash,
            bundle.UserDevicesData.IntegrityHash,
            typeof(UserDevicesData));
    }


    public void VerifyUntrustedBundle(UserDataBundle bundle)
    {
        VerifyUserData(bundle.UserData);
        VerifyGeneralUserData(bundle.GeneralUserData);
        VerifyUserPasswordsData(bundle.UserPasswordsData);
        VerifyUserDevicesData(bundle.UserDevicesData);
        VerifyBundleLinks(bundle);
    }


    public void RebuildInitialIntegrity(UserDataBundle bundle)
    {
        SyncVersionStampTraversal.Validate(bundle.GeneralUserData);
        SyncVersionStampTraversal.Validate(bundle.UserPasswordsData);
        SyncVersionStampTraversal.Validate(bundle.UserDevicesData);
        Canonicalize(bundle.UserPasswordsData);
        Canonicalize(bundle.UserDevicesData);
        bundle.GeneralUserData.GenerateIntegrityHash();
        RebuildUserPasswordsDataIntegrity(bundle.UserPasswordsData);
        RebuildUserDevicesDataIntegrity(bundle.UserDevicesData);
        CopyChildHashesToUserData(bundle, UserDataBlobKind.All);
        bundle.UserData.GenerateIntegrityHash();
        VerifyBundleLinks(bundle);
    }


    public void UpdateModifiedBlobIntegrity(UserDataBundle bundle, UserDataBlobKind modifiedBlobs)
    {
        if (modifiedBlobs.HasFlag(UserDataBlobKind.General))
        {
            SyncVersionStampTraversal.Validate(bundle.GeneralUserData);
            bundle.GeneralUserData.GenerateIntegrityHash();
        }

        if (modifiedBlobs.HasFlag(UserDataBlobKind.Passwords))
        {
            SyncVersionStampTraversal.Validate(bundle.UserPasswordsData);
            Canonicalize(bundle.UserPasswordsData);
            VerifyPasswordChildren(bundle.UserPasswordsData);
            bundle.UserPasswordsData.GenerateIntegrityHash();
        }

        if (modifiedBlobs.HasFlag(UserDataBlobKind.Devices))
        {
            SyncVersionStampTraversal.Validate(bundle.UserDevicesData);
            Canonicalize(bundle.UserDevicesData);
            VerifyDeviceChildren(bundle.UserDevicesData);
            bundle.UserDevicesData.GenerateIntegrityHash();
        }

        CopyChildHashesToUserData(bundle, modifiedBlobs);
        bundle.UserData.GenerateIntegrityHash();
        VerifyBundleLinks(bundle);
    }


    public void RebuildModifiedBlobIntegrity(UserDataBundle bundle, UserDataBlobKind modifiedBlobs)
    {
        if (modifiedBlobs.HasFlag(UserDataBlobKind.General))
        {
            SyncVersionStampTraversal.Validate(bundle.GeneralUserData);
            bundle.GeneralUserData.GenerateIntegrityHash();
        }

        if (modifiedBlobs.HasFlag(UserDataBlobKind.Passwords))
        {
            SyncVersionStampTraversal.Validate(bundle.UserPasswordsData);
            Canonicalize(bundle.UserPasswordsData);
            RebuildUserPasswordsDataIntegrity(bundle.UserPasswordsData);
        }

        if (modifiedBlobs.HasFlag(UserDataBlobKind.Devices))
        {
            SyncVersionStampTraversal.Validate(bundle.UserDevicesData);
            Canonicalize(bundle.UserDevicesData);
            RebuildUserDevicesDataIntegrity(bundle.UserDevicesData);
        }

        CopyChildHashesToUserData(bundle, modifiedBlobs);
        bundle.UserData.GenerateIntegrityHash();
        VerifyBundleLinks(bundle);
    }



    private void Canonicalize(UserPasswordsData data)
    {
        foreach (var password in data.Passwords)
            password.TagIds = password.TagIds.Order().ToList();
        data.Passwords = data.Passwords.OrderBy(item => item.Id).ToList();
        data.DeletedPasswords = data.DeletedPasswords.OrderBy(item => item.Id).ToList();
        data.CustomColors = data.CustomColors.OrderBy(item => item.Id).ToList();
        data.DeletedCustomColors = data.DeletedCustomColors.OrderBy(item => item.Id).ToList();
        data.Tags = data.Tags.OrderBy(item => item.Id).ToList();
        data.DeletedTags = data.DeletedTags.OrderBy(item => item.Id).ToList();
    }

    private void Canonicalize(UserDevicesData data)
    {
        data.Devices = data.Devices.OrderBy(item => item.Id).ToList();
        data.DeletedDevices = data.DeletedDevices.OrderBy(item => item.Id).ToList();
    }

    private void RebuildUserPasswordsDataIntegrity(UserPasswordsData userPasswordsData)
    {
        foreach (var password in userPasswordsData.Passwords)
            password.GenerateIntegrityHash();
        foreach (var deleted in userPasswordsData.DeletedPasswords)
            deleted.GenerateIntegrityHash();
        foreach (var color in userPasswordsData.CustomColors)
            color.GenerateIntegrityHash();
        foreach (var deleted in userPasswordsData.DeletedCustomColors)
            deleted.GenerateIntegrityHash();
        foreach (var tag in userPasswordsData.Tags)
            tag.GenerateIntegrityHash();
        foreach (var deleted in userPasswordsData.DeletedTags)
            deleted.GenerateIntegrityHash();

        userPasswordsData.GenerateIntegrityHash();
    }


    private void RebuildUserDevicesDataIntegrity(UserDevicesData userDevicesData)
    {
        foreach (var device in userDevicesData.Devices)
            device.GenerateIntegrityHash();
        foreach (var deleted in userDevicesData.DeletedDevices)
            deleted.GenerateIntegrityHash();

        userDevicesData.GenerateIntegrityHash();
    }


    private void VerifyPasswordChildren(UserPasswordsData userPasswordsData)
    {
        foreach (var password in userPasswordsData.Passwords)
            password.VerifyIntegrity();
        foreach (var deleted in userPasswordsData.DeletedPasswords)
            deleted.VerifyIntegrity();
        foreach (var color in userPasswordsData.CustomColors)
            color.VerifyIntegrity();
        foreach (var deleted in userPasswordsData.DeletedCustomColors)
            deleted.VerifyIntegrity();
        foreach (var tag in userPasswordsData.Tags)
            tag.VerifyIntegrity();
        foreach (var deleted in userPasswordsData.DeletedTags)
            deleted.VerifyIntegrity();
    }


    private void VerifyDeviceChildren(UserDevicesData userDevicesData)
    {
        foreach (var device in userDevicesData.Devices)
            device.VerifyIntegrity();
        foreach (var deleted in userDevicesData.DeletedDevices)
            deleted.VerifyIntegrity();
    }


    private void CopyChildHashesToUserData(UserDataBundle bundle, UserDataBlobKind modifiedBlobs)
    {
        if (modifiedBlobs.HasFlag(UserDataBlobKind.General))
            bundle.UserData.GeneralUserDataIntegrityHash = ReplaceHash(
                bundle.UserData.GeneralUserDataIntegrityHash,
                bundle.GeneralUserData.IntegrityHash);

        if (modifiedBlobs.HasFlag(UserDataBlobKind.Passwords))
            bundle.UserData.UserPasswordsDataIntegrityHash = ReplaceHash(
                bundle.UserData.UserPasswordsDataIntegrityHash,
                bundle.UserPasswordsData.IntegrityHash);

        if (modifiedBlobs.HasFlag(UserDataBlobKind.Devices))
            bundle.UserData.UserDevicesDataIntegrityHash = ReplaceHash(
                bundle.UserData.UserDevicesDataIntegrityHash,
                bundle.UserDevicesData.IntegrityHash);
    }


    private byte[] ReplaceHash(byte[] currentHash, byte[] sourceHash)
    {
        CryptographicOperations.ZeroMemory(currentHash);
        return sourceHash.ToArray();
    }


    private void VerifyStoredChildHash(byte[] expected, byte[] actual, Type type)
    {
        if (expected.Length != CryptographyConstants.Sha256HashSizeInBytes ||
            actual.Length != CryptographyConstants.Sha256HashSizeInBytes ||
            !Hashing.Verify(expected, actual))
            throw new InvalidDataIntegrityException(type);
    }
}
