using PasswordManagerLocal.Common.Backend.Models.Encrypted;

namespace PasswordManagerLocal.Common.Backend.Abstractions.Services;

public interface IUserDataBundleIntegrityService
{
    void VerifyUserData(UserData userData);
    void VerifyGeneralUserData(GeneralUserData generalUserData);
    void VerifyUserPasswordsData(UserPasswordsData userPasswordsData);
    void VerifyUserDevicesData(UserDevicesData userDevicesData);
    void VerifyBundleLinks(UserDataBundle bundle);

    /// <summary>
    /// Recursively verifies every integrity-protected object and every root-to-child hash link.
    /// Use this for data loaded from storage, cache, or another device before it is trusted.
    /// </summary>
    void VerifyUntrustedBundle(UserDataBundle bundle);

    /// <summary>
    /// Builds all integrity hashes for a newly created bundle.
    /// </summary>
    void RebuildInitialIntegrity(UserDataBundle bundle);

    /// <summary>
    /// Updates aggregate and root hashes for trusted local mutations. Existing child hashes are
    /// verified before aggregate hashes are rebuilt so a forgotten child hash update is rejected.
    /// </summary>
    void UpdateModifiedBlobIntegrity(UserDataBundle bundle, UserDataBlobKind modifiedBlobs);

    /// <summary>
    /// Rebuilds integrity only inside blobs explicitly changed by a merge of already-verified data.
    /// </summary>
    void RebuildModifiedBlobIntegrity(UserDataBundle bundle, UserDataBlobKind modifiedBlobs);
}
