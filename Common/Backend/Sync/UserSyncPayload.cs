using PasswordManagerLocal.Common.Backend.Models;
using PasswordManagerLocal.Common.Backend.Models.Encrypted;

namespace PasswordManagerLocal.Common.Backend.Sync;

public sealed class UserSyncPayload
{
    public Guid UId { get; set; }
    public byte[] UsernameHash { get; set; } = [];
    public byte[] UsernameSalt { get; set; } = [];
    public SyncVersionStamp GeneralUserDataVersion { get; set; } = new();
    public byte[] PasswordSalt { get; set; } = [];
    public byte[] EncryptedPayload { get; set; } = [];
    public byte[] EncryptedGeneralUserDataPayload { get; set; } = [];
    public byte[] EncryptedUserPasswordsDataPayload { get; set; } = [];
    public byte[] EncryptedUserDevicesDataPayload { get; set; } = [];
    public DateTimeOffset UserDataLastModifiedAt { get; set; }
    public DateTimeOffset GeneralUserDataLastModifiedAt { get; set; }
    public DateTimeOffset UserPasswordsDataLastModifiedAt { get; set; }
    public DateTimeOffset UserDevicesDataLastModifiedAt { get; set; }
    public byte[] IntegrityHash { get; set; } = [];
    public List<Guid> GroupIds { get; set; } = [];
    public List<Guid> DeviceIds { get; set; } = [];
}
