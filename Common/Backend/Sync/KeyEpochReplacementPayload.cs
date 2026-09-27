using PasswordManagerLocal.Common.Backend.Models.Encrypted;

namespace PasswordManagerLocal.Common.Backend.Sync;

/// <summary>
/// Complete encrypted canonical account replacement. It contains no plaintext password,
/// password-derived raw key, blob key, SavedKey, or decrypted account data.
/// </summary>
public sealed class KeyEpochReplacementPayload
{
    public Guid UserId { get; set; }
    public long PreviousKeyEpoch { get; set; }
    public long ResultingKeyEpoch { get; set; }
    public long MembershipEpoch { get; set; }
    public byte[] UsernameHash { get; set; } = [];
    public byte[] UsernameSalt { get; set; } = [];
    public SyncVersionStamp GeneralUserDataVersion { get; set; } = new();
    public byte[] PasswordSalt { get; set; } = [];
    public byte[] EncryptedPayload { get; set; } = [];
    public byte[] EncryptedGeneralUserDataPayload { get; set; } = [];
    public byte[] EncryptedUserPasswordsDataPayload { get; set; } = [];
    public byte[] EncryptedUserDevicesDataPayload { get; set; } = [];
    public DateTimeOffset LastModifiedAt { get; set; }
    public DateTimeOffset UserDataLastModifiedAt { get; set; }
    public DateTimeOffset GeneralUserDataLastModifiedAt { get; set; }
    public DateTimeOffset UserPasswordsDataLastModifiedAt { get; set; }
    public DateTimeOffset UserDevicesDataLastModifiedAt { get; set; }
    public byte[] UserIntegrityHash { get; set; } = [];
}
